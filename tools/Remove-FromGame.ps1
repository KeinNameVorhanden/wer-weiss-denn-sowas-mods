<#
.SYNOPSIS
  Removes individual files that Install-ToGame.ps1 added (listed in the manifest) and updates the
  manifest. Files not owned by the manifest are refused. Absolute paths only.
.PARAMETER GameDir  Absolute game root.
.PARAMETER Path     Paths relative to GameDir, e.g. BepInEx\plugins\WWDS.Diagnostics\WWDS.Diagnostics.dll
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [Parameter(Mandatory = $true)][string[]]$Path
)

$ErrorActionPreference = 'Stop'
$GameDir = (Resolve-Path -LiteralPath $GameDir).Path.TrimEnd('\')
$manifestPath = Join-Path $GameDir '.wwds-install-manifest.txt'
$lines = [System.Collections.Generic.List[string]]::new([string[]](Get-Content -LiteralPath $manifestPath))

foreach ($rel in $Path) {
    $entry = "F|$rel"
    $index = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ([string]::Equals($lines[$i], $entry, [StringComparison]::OrdinalIgnoreCase)) { $index = $i; break } }
    if ($index -lt 0) { throw "Not installed by the harness (not in manifest): $rel" }
    $abs = [System.IO.Path]::GetFullPath((Join-Path $GameDir $rel))
    if (-not $abs.StartsWith($GameDir + '\', [StringComparison]::OrdinalIgnoreCase)) { throw "Path escapes game dir: $abs" }
    if ((Test-Path -LiteralPath $abs) -and $PSCmdlet.ShouldProcess($abs, 'Delete file')) { Remove-Item -LiteralPath $abs -Force }
    $lines.RemoveAt($index)
    "Removed $abs"
}

if ($PSCmdlet.ShouldProcess($manifestPath, 'Update manifest')) { Set-Content -LiteralPath $manifestPath -Value $lines -Encoding UTF8 }
