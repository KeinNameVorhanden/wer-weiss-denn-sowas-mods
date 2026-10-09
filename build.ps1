<#
.SYNOPSIS
  Builds the harness, runs tests, stages the in-game payload under dist/, and optionally deploys it.

.PARAMETER Configuration  Debug or Release (default Release).
.PARAMETER SkipTests      Do not run unit tests.
.PARAMETER Deploy         Copy the staged payload into the detected game installs (manifest-tracked).
#>
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$Deploy
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location -LiteralPath $root

if (-not (Test-Path -LiteralPath (Join-Path $root 'build\local.props'))) {
    & (Join-Path $root 'tools\Find-Games.ps1')
}

dotnet build (Join-Path $root 'WWDS.sln') -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'tests\WWDS.Tests\WWDS.Tests.csproj') -c $Configuration --no-build --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed' }
}

python -I (Join-Path $root 'tools\check_text.py') $root
if ($LASTEXITCODE -ne 0) { throw 'Text check failed (em/en dashes or non UTF-8)' }

$stage = Join-Path $root 'tools\Stage-Payload.ps1'
if (Test-Path -LiteralPath $stage) {
    & $stage -Configuration $Configuration
    if ($Deploy) { & (Join-Path $root 'tools\Deploy-ToGames.ps1') }
}
