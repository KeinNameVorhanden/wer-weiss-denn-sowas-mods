<#
.SYNOPSIS
  Extracts one or more zip packages (or copies a folder) into a game directory without overwriting
  any pre-existing file. Every added file and every newly created directory is recorded in
  <GameDir>\.wwds-install-manifest.txt so Uninstall-FromGame.ps1 can remove exactly those.

.PARAMETER GameDir   Absolute game root (folder containing the game .exe).
.PARAMETER Zip       Zip files to extract into GameDir (archive root maps to GameDir).
.PARAMETER Folder    Folders whose content is copied into GameDir (folder root maps to GameDir).
.PARAMETER Update    Allow replacing files that are already listed in the manifest (our own files).
#>
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$GameDir,
    [string[]]$Zip = @(),
    [string[]]$Folder = @(),
    [switch]$Update
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem

$GameDir = (Resolve-Path -LiteralPath $GameDir).Path.TrimEnd('\')
if (-not (Get-ChildItem -LiteralPath $GameDir -Filter '*_Data' -Directory)) { throw "Not a Unity game dir: $GameDir" }
$manifestPath = Join-Path $GameDir '.wwds-install-manifest.txt'

# Manifest lines: "F|<relative path>" for files, "D|<relative path>" for directories we created.
$manifest = New-Object System.Collections.Generic.List[string]
if (Test-Path -LiteralPath $manifestPath) { $manifest.AddRange([string[]](Get-Content -LiteralPath $manifestPath)) }
$owned = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
foreach ($l in $manifest) { [void]$owned.Add($l) }

function Add-ManifestEntry([string]$kind, [string]$rel) {
    $line = "$kind|$rel"
    if ($owned.Add($line)) { $manifest.Add($line) }
}

function Ensure-Dir([string]$rel) {
    if ([string]::IsNullOrEmpty($rel)) { return }
    $parent = Split-Path -Parent $rel
    Ensure-Dir $parent
    $abs = Join-Path $GameDir $rel
    if (-not (Test-Path -LiteralPath $abs)) {
        if ($PSCmdlet.ShouldProcess($abs, 'Create directory')) { [void][System.IO.Directory]::CreateDirectory($abs) }
        Add-ManifestEntry 'D' $rel
    }
}

function Install-File([string]$rel, [scriptblock]$writer) {
    $abs = Join-Path $GameDir $rel
    if (Test-Path -LiteralPath $abs) {
        if (-not ($Update -and $owned.Contains("F|$rel"))) {
            throw "Refusing to overwrite existing file not owned by this installer: $abs"
        }
    }
    Ensure-Dir (Split-Path -Parent $rel)
    if ($PSCmdlet.ShouldProcess($abs, 'Write file')) { & $writer $abs }
    Add-ManifestEntry 'F' $rel
}

# Pre-flight: detect conflicts before touching anything.
$plan = New-Object System.Collections.Generic.List[object]
foreach ($z in $Zip) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $z).Path)
    try {
        foreach ($e in $archive.Entries) {
            if ($e.FullName.EndsWith('/')) { continue }
            $plan.Add([pscustomobject]@{ Rel = $e.FullName.Replace('/', '\'); Zip = $z; Entry = $e.FullName; Source = $null })
        }
    } finally { $archive.Dispose() }
}
foreach ($f in $Folder) {
    $root = (Resolve-Path -LiteralPath $f).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $plan.Add([pscustomobject]@{ Rel = $_.FullName.Substring($root.Length + 1); Zip = $null; Entry = $null; Source = $_.FullName })
    }
}
$conflicts = $plan | Where-Object { (Test-Path -LiteralPath (Join-Path $GameDir $_.Rel)) -and -not ($Update -and $owned.Contains("F|$($_.Rel)")) }
if ($conflicts) {
    $conflicts | ForEach-Object { Write-Warning "Conflict: $($_.Rel)" }
    throw 'Aborted: files would be overwritten. Nothing was changed.'
}

foreach ($p in $plan) {
    if ($p.Zip) {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $p.Zip).Path)
        try {
            $entry = $archive.GetEntry($p.Entry)
            Install-File $p.Rel { param($abs) [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $abs, $true) }
        } finally { $archive.Dispose() }
    } else {
        $src = $p.Source
        Install-File $p.Rel { param($abs) Copy-Item -LiteralPath $src -Destination $abs -Force }
    }
}

if ($PSCmdlet.ShouldProcess($manifestPath, 'Write manifest')) {
    Set-Content -LiteralPath $manifestPath -Value $manifest -Encoding UTF8
}
"Installed $($plan.Count) files into $GameDir (manifest: $manifestPath)"
