<#
.SYNOPSIS
  Publishes the current version: builds and tests locally (the in-game plugins need the installed games),
  packages the release zip, adds one commit with the current source tree to the public branch, pushes it
  to GitHub as main and creates the GitHub release vX.Y.Z with the zip attached.

  The public branch never contains the private development history: each release is a single commit
  whose parent is the previous release. Run it from the private branch (master) with a clean working tree.

  One-time setup (local git config, not committed):
    git config wwds.publicName  "Your Name"
    git config wwds.publicEmail "12345+you@users.noreply.github.com"
    git remote add origin https://github.com/<you>/<repo>.git

.PARAMETER NotesFile  Markdown release notes; default: install hint plus the feat/fix commit subjects
                      since the previous version tag.
.PARAMETER DryRun     Build and prepare everything, but push and release nothing.
#>
param(
    [string]$NotesFile,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $root

function Invoke-Git([string[]]$gitArgs) {
    $out = & git.exe @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') failed" }
    return $out
}

# ---- preconditions --------------------------------------------------------------------------------
$branch = (Invoke-Git @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
if ($branch -eq 'public') { throw 'Run this from the private branch (master), not from public.' }
if (Invoke-Git @('status', '--porcelain')) { throw 'Commit or stash your changes first (the release is built from a clean tree).' }
$name = (& git.exe config wwds.publicName)
$email = (& git.exe config wwds.publicEmail)
if (-not $name -or -not $email) { throw 'Set git config wwds.publicName and wwds.publicEmail (see the script header).' }

[xml]$props = Get-Content -LiteralPath (Join-Path $root 'Directory.Build.props')
$version = $props.Project.PropertyGroup[0].Version
$tag = "v$version"
& gh release view $tag 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) { throw "GitHub release $tag already exists. Bump the version first." }

# Never publish private material, even if it was committed by mistake.
$tree = (Invoke-Git @('rev-parse', 'HEAD^{tree}')).Trim()
$files = Invoke-Git @('ls-tree', '-r', '--name-only', $tree)
$forbidden = $files | Where-Object { $_ -match '^(_devnotes|_re|_extract|_downloads|artifacts|dist)/' -or $_ -eq 'build/local.props' }
if ($forbidden) { throw "Refusing to publish: $($forbidden -join ', ')" }

# ---- build, test, package -------------------------------------------------------------------------
& (Join-Path $root 'build.ps1')
& (Join-Path $PSScriptRoot 'Package-Release.ps1') | Write-Host
$zip = Get-ChildItem -LiteralPath (Join-Path $root 'dist') -Filter "*-$version.zip" | Select-Object -First 1
if (-not $zip) { throw "Release zip for $version not found in dist/" }

# ---- public commit ----------------------------------------------------------------------------------
& git.exe fetch --quiet origin main 2>$null
$parent = $null
if ((& git.exe rev-parse --verify --quiet refs/heads/public)) { $parent = (Invoke-Git @('rev-parse', 'refs/heads/public')).Trim() }
if ($parent -and (Invoke-Git @('rev-parse', "$parent^{tree}")).Trim() -eq $tree) {
    $commit = $parent
    "Public branch already has this source tree ($($commit.Substring(0, 7)))."
}
else {
    $env:GIT_AUTHOR_NAME = $name; $env:GIT_AUTHOR_EMAIL = $email
    $env:GIT_COMMITTER_NAME = $name; $env:GIT_COMMITTER_EMAIL = $email
    try {
        $commitArgs = @('commit-tree', $tree, '-m', "WWDS Mods $version")
        if ($parent) { $commitArgs += @('-p', $parent) }
        $commit = (Invoke-Git $commitArgs).Trim()
    }
    finally {
        Remove-Item Env:GIT_AUTHOR_NAME, Env:GIT_AUTHOR_EMAIL, Env:GIT_COMMITTER_NAME, Env:GIT_COMMITTER_EMAIL -ErrorAction SilentlyContinue
    }
    if (-not $DryRun) { Invoke-Git @('update-ref', 'refs/heads/public', $commit) | Out-Null }
    "Public commit $($commit.Substring(0, 7)) (parent $(if ($parent) { $parent.Substring(0, 7) } else { 'none' }))."
}

# ---- release notes --------------------------------------------------------------------------------
if (-not $NotesFile) {
    $previous = Invoke-Git @('tag', '--merged', 'HEAD', '--sort=-v:refname') | Where-Object { $_ -match '^v\d+\.\d+\.\d+$' -and $_ -ne $tag } | Select-Object -First 1
    $range = if ($previous) { "$previous..HEAD" } else { 'HEAD' }
    $changes = Invoke-Git @('log', '--format=%s', $range) | Where-Object { $_ -match '^(feat|fix)(\(.+\))?: ' } |
        ForEach-Object { '- ' + ($_ -replace '^(feat|fix)(\(.+\))?: ', '') }
    $notes = @(
        "Download **$($zip.Name)**, unpack it, close the games and double-click **Install.cmd**.",
        'It finds both games through Steam, installs BepInEx if needed and installs the mods. Uninstall.cmd removes them again.',
        ''
    )
    if ($changes) { $notes += @('## Changes', '') + $changes }
    $NotesFile = Join-Path $root "dist\release-notes-$version.md"
    Set-Content -LiteralPath $NotesFile -Value $notes -Encoding UTF8
}

if ($DryRun) {
    "Dry run: would push $($commit.Substring(0, 7)) to origin/main and create release $tag with $($zip.Name)."
    "Notes: $NotesFile"
    return
}

# ---- publish ----------------------------------------------------------------------------------------
# Only the public branch goes out, never tags (local version tags point at private commits).
Invoke-Git @('push', '--no-tags', 'origin', "$($commit):refs/heads/main") | Out-Null
& gh release create $tag $zip.FullName --target $commit --title "WWDS Mods $version" --notes-file $NotesFile
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed' }
"Released $tag."
