param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BepInExSource = 'D:\code\annw-lan\AnnW.LanMp-0.18.0-with-BepInEx',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'),
    [string]$ArtifactsPath = ''
)

$ErrorActionPreference = 'Stop'

$required = @(
    (Join-Path $BepInExSource 'BepInEx\core\BepInEx.dll'),
    (Join-Path $BepInExSource 'winhttp.dll'),
    (Join-Path $BepInExSource 'doorstop_config.ini')
)
foreach ($path in $required) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Invalid BepInEx source: missing $path" }
}

& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -ArtifactsPath $ArtifactsPath

$pluginSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'src\XingyiStarry.Mp\XingyiStarryMpPlugin.cs')
$match = [regex]::Match($pluginSource, 'PluginVersion\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'Could not determine PluginVersion.' }
$version = $match.Groups[1].Value

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$staging = [IO.Path]::GetFullPath((Join-Path $outputRoot "staging-$version"))
if (-not $staging.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe staging path: $staging"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

$coreTarget = Join-Path $staging 'BepInEx\core'
$pluginTarget = Join-Path $staging 'BepInEx\plugins\XingyiStarry.Mp'
$configTarget = Join-Path $staging 'BepInEx\config'
New-Item -ItemType Directory -Force -Path $coreTarget, $pluginTarget, $configTarget | Out-Null
Copy-Item -Path (Join-Path $BepInExSource 'BepInEx\core\*') -Destination $coreTarget -Recurse -Force
Copy-Item -LiteralPath (Join-Path $BepInExSource 'winhttp.dll') -Destination $staging
Copy-Item -LiteralPath (Join-Path $BepInExSource 'doorstop_config.ini') -Destination $staging
if (Test-Path -LiteralPath (Join-Path $BepInExSource '.doorstop_version')) {
    Copy-Item -LiteralPath (Join-Path $BepInExSource '.doorstop_version') -Destination $staging
}
Copy-Item -Path (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\XingyiStarry.Mp\*') -Destination $pluginTarget -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config\relay-default.cfg') -Destination (Join-Path $configTarget 'xingyistarry.mp.cfg')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $staging 'XingyiStarry.Mp-README.txt')

$archive = Join-Path $outputRoot "XingyiStarry-MP-$version-with-BepInEx.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force

$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Host "Package ready: $archive"
Write-Host "SHA256: $($hash.Hash)"
