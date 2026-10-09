<#
.SYNOPSIS
  Collects the in-game harness assemblies into artifacts/payload (BepInEx layout) and the bridge into
  artifacts/bridge. Run after a build (build.ps1 calls it).
#>
param(
    [string]$Configuration = 'Release',
    # Twitch application Client ID (Client Type: Public) to bake into the bridge; empty = use the
    # WwdsTwitchClientId environment variable or build/local.props, if any.
    [string]$TwitchClientId = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$clientIdArg = @()
if ($TwitchClientId.Trim().Length -gt 0) { $clientIdArg = @("-p:WwdsTwitchClientId=$($TwitchClientId.Trim())") }
$payload = Join-Path $root 'artifacts\payload'
$plugin = Join-Path $payload 'BepInEx\plugins\WWDS'
if (Test-Path -LiteralPath $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }
New-Item -ItemType Directory -Force $plugin | Out-Null

$hostBin = Join-Path $root "src\Loader\WWDS.Host.BepInEx\bin\$Configuration\net35"
$wanted = @('WWDS.Host.BepInEx.dll', 'WWDS.ModCore.dll', 'WWDS.Runtime.dll', 'WWDS.Game.Wwds.dll', 'WWDS.Game.Wwds.Mods.dll', 'WWDS.Features.dll',
    'WWDS.Twitch.dll', 'WWDS.Protocol.dll', 'WWDS.Audience.dll', 'WWDS.UI.dll')
foreach ($name in $wanted) {
    $src = Join-Path $hostBin $name
    if (Test-Path -LiteralPath $src) { Copy-Item -LiteralPath $src -Destination $plugin }
    elseif ($name -in @('WWDS.Host.BepInEx.dll', 'WWDS.ModCore.dll', 'WWDS.Game.Wwds.dll')) { throw "Missing $src (build first)" }
}

# Never ship game, Unity, BepInEx or Harmony assemblies.
Get-ChildItem -LiteralPath $plugin | Where-Object { $_.Name -match '^(Assembly-CSharp|UnityEngine|BepInEx|0Harmony|mscorlib|System)' } | ForEach-Object { throw "Refusing to stage $($_.Name)" }

# In-game login starts the bridge from the plugin folder: ship it self-contained (no .NET install needed).
$embedded = Join-Path $plugin 'bridge'
dotnet publish (Join-Path $root 'src\Bridge\WWDS.Bridge\WWDS.Bridge.csproj') -c $Configuration -o $embedded --nologo -v quiet `
    -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -p:RuntimeFrameworkVersion=8.0.28 -p:DebugType=none @clientIdArg | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Embedded bridge publish failed (an invalid Twitch Client ID also stops the build)' }

$bridgeOut = Join-Path $root 'artifacts\bridge'
if (Test-Path -LiteralPath $bridgeOut) { Remove-Item -LiteralPath $bridgeOut -Recurse -Force }
dotnet publish (Join-Path $root 'src\Bridge\WWDS.Bridge\WWDS.Bridge.csproj') -c $Configuration -o $bridgeOut --nologo -v quiet @clientIdArg | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Bridge publish failed' }

# Report what was baked in (read back from the generated source of the embedded build).
$generated = Get-Item -LiteralPath (Join-Path $root "src\Bridge\WWDS.Bridge\obj\$Configuration\net8.0\win-x64\BuiltInTwitch.g.cs") -ErrorAction SilentlyContinue
$baked = if ($generated) { [regex]::Match((Get-Content -LiteralPath $generated.FullName -Raw), 'ClientId = "([a-z0-9]*)"').Groups[1].Value } else { '' }
if ($baked.Length -gt 0) { "Twitch Client ID built in: $($baked.Substring(0, 4))...$($baked.Substring($baked.Length - 4))" }
else { 'Twitch Client ID: none built in (players enter their own)' }

"Staged payload: $plugin"
Get-ChildItem -LiteralPath $plugin | ForEach-Object { '  {0,-28} {1,8}' -f $_.Name, $_.Length }
"Staged bridge:  $bridgeOut"
