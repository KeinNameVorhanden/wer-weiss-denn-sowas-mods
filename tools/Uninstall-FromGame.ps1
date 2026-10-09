<#
.SYNOPSIS
  Removes exactly what Install-ToGame.ps1 added to a game directory, using absolute paths only.
  Directories the installer created (for example BepInEx\) are removed with all content that
  was generated inside them at runtime (logs, config, cache), because they did not exist before.
  Pre-existing game files are never touched.

.PARAMETER GameDir  Absolute game root.
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param([Parameter(Mandatory = $true)][string]$GameDir)

$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path.TrimEnd('\')
if (-not [System.IO.Path]::IsPathRooted($GameDir)) { throw "GameDir must be absolute: $GameDir" }
$manifestPath = Join-Path $GameDir '.wwds-install-manifest.txt'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "No manifest at $manifestPath" }

$lines = Get-Content -LiteralPath $manifestPath
$files = $lines | Where-Object { $_.StartsWith('F|') } | ForEach-Object { $_.Substring(2) }
$dirs = $lines | Where-Object { $_.StartsWith('D|') } | ForEach-Object { $_.Substring(2) }

foreach ($rel in $files) {
    $abs = [System.IO.Path]::GetFullPath((Join-Path $GameDir $rel))
    if (-not $abs.StartsWith($GameDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes game dir: $abs" }
    if (Test-Path -LiteralPath $abs) {
        if ($PSCmdlet.ShouldProcess($abs, 'Delete file')) { Remove-Item -LiteralPath $abs -Force }
    }
}

# Deepest first; created directories go entirely (they hold only our files and runtime output).
foreach ($rel in ($dirs | Sort-Object { $_.Length } -Descending)) {
    $abs = [System.IO.Path]::GetFullPath((Join-Path $GameDir $rel))
    if (-not $abs.StartsWith($GameDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes game dir: $abs" }
    if (Test-Path -LiteralPath $abs) {
        if ($PSCmdlet.ShouldProcess($abs, 'Delete directory tree')) { Remove-Item -LiteralPath $abs -Recurse -Force }
    }
}

if ($PSCmdlet.ShouldProcess($manifestPath, 'Delete manifest')) { Remove-Item -LiteralPath $manifestPath -Force }
"Uninstalled from $GameDir"
