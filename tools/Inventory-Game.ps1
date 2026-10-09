<#
.SYNOPSIS
  Prints a compact technical inventory of a Unity game installation.
.PARAMETER GameDir
  Root folder of the installed game (the folder that contains the .exe).
#>
param([Parameter(Mandatory = $true)][string]$GameDir)

$ErrorActionPreference = 'Stop'
$data = Get-ChildItem -LiteralPath $GameDir -Directory -Filter '*_Data' | Select-Object -First 1
if (-not $data) { throw "No *_Data folder in $GameDir" }
$exe = Get-ChildItem -LiteralPath $GameDir -Filter '*.exe' | Where-Object Name -NotMatch 'Crash' | Select-Object -First 1

function Get-PeMachine([string]$path) {
    $fs = [System.IO.File]::OpenRead($path)
    try {
        $br = New-Object System.IO.BinaryReader($fs)
        $fs.Position = 0x3C; $pe = $br.ReadInt32()
        $fs.Position = $pe + 4; $m = $br.ReadUInt16()
        switch ($m) { 0x14c { 'x86' } 0x8664 { 'x64' } default { '0x{0:X}' -f $m } }
    } finally { $fs.Dispose() }
}

function Get-UnityVersionFromGgm([string]$path) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $text = [System.Text.Encoding]::ASCII.GetString($bytes, 0, [Math]::Min(256, $bytes.Length))
    $m = [regex]::Match($text, '20\d\d\.\d+\.\d+[abfp]\d+')
    if ($m.Success) { $m.Value } else { 'unknown' }
}

"Game dir     : $GameDir"
"Exe          : $($exe.Name)  ($(Get-PeMachine $exe.FullName))"
"Data dir     : $($data.Name)"
"Unity (ggm)  : $(Get-UnityVersionFromGgm (Join-Path $data.FullName 'globalgamemanagers'))"
"UnityPlayer  : $((Get-Item (Join-Path $GameDir 'UnityPlayer.dll')).VersionInfo.FileVersion)"
"app.info     : $((Get-Content (Join-Path $data.FullName 'app.info')) -join ' | ')"
$monoDirs = @('Mono', 'MonoBleedingEdge') | Where-Object { Test-Path (Join-Path $GameDir $_) }
"Mono runtime : $($monoDirs -join ', ')"
$monoDll = Get-ChildItem -LiteralPath $GameDir -Recurse -Include 'mono.dll', 'mono-2.0-bdwgc.dll', 'mono-2.0-sgen.dll' -ErrorAction SilentlyContinue
foreach ($d in $monoDll) { "  $($d.FullName.Substring($GameDir.Length))  $($d.Length) bytes  $($d.VersionInfo.FileVersion)" }
if (Test-Path (Join-Path $GameDir 'GameAssembly.dll')) { 'IL2CPP       : YES (GameAssembly.dll present)' } else { 'IL2CPP       : no GameAssembly.dll' }

"`n-- Managed assemblies"
$managed = Join-Path $data.FullName 'Managed'
Get-ChildItem -LiteralPath $managed -Filter '*.dll' | Sort-Object Name | ForEach-Object {
    $ver = try { [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName).Version.ToString() } catch { 'n/a' }
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.Substring(0, 16)
    '  {0,-55} {1,10}  v{2,-14} sha256:{3}' -f $_.Name, $_.Length, $ver, $hash
}

"`n-- Native plugins"
Get-ChildItem -LiteralPath (Join-Path $data.FullName 'Plugins') -Recurse -File | ForEach-Object { '  {0,-45} {1,10}  {2}' -f $_.Name, $_.Length, (Get-PeMachine $_.FullName) }

"`n-- StreamingAssets (grouped by top-level folder / extension)"
$sa = Join-Path $data.FullName 'StreamingAssets'
Get-ChildItem -LiteralPath $sa -Recurse -File | ForEach-Object {
    $rel = $_.FullName.Substring($sa.Length + 1)
    $parts = $rel.Split([System.IO.Path]::DirectorySeparatorChar)
    $group = if ($parts.Length -gt 2) { ($parts[0..($parts.Length - 2)] -join '/') } elseif ($parts.Length -eq 2) { $parts[0] } else { '(root)' }
    $ext = if ($_.Extension) { $_.Extension.ToLowerInvariant() } else { '(none)' }
    [pscustomobject]@{ Group = $group; Ext = $ext; Size = $_.Length; Name = $_.Name }
} | Group-Object Group, Ext | Sort-Object Name | ForEach-Object {
    $sum = ($_.Group | Measure-Object Size -Sum).Sum
    $sample = ($_.Group | Select-Object -First 3 | ForEach-Object Name) -join ', '
    '  {0,-40} files={1,4}  MB={2,8:N1}  e.g. {3}' -f $_.Name, $_.Count, ($sum / 1MB), $sample
}

"`n-- Resources folder"
Get-ChildItem -LiteralPath (Join-Path $data.FullName 'Resources') -Recurse -File | ForEach-Object { '  {0,-45} {1,10}' -f $_.Name, $_.Length }

"`n-- Top-level data files"
Get-ChildItem -LiteralPath $data.FullName -File | ForEach-Object { '  {0,-35} {1,12}' -f $_.Name, $_.Length }
