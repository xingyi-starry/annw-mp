param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BepInExSource = (Join-Path $PSScriptRoot '.deps\BepInEx_win_x64_5.4.23.5'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'),
    [string]$GameRoot = '',
    [string]$ArtifactsPath = ''
)

$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'tools\Ensure-BepInEx.ps1') -Destination $BepInExSource
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -GameRoot $GameRoot -ArtifactsPath $ArtifactsPath

$pluginSource = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'src\XingyiStarry.Mp\XingyiStarryMpPlugin.cs')
$match = [regex]::Match($pluginSource, 'PluginVersion\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'Could not determine PluginVersion.' }
$version = $match.Groups[1].Value

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null

function Assert-SafeStagingPath([string]$Path) {
    if (-not $Path.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unsafe staging path: $Path"
    }
}

function Add-PluginFiles([string]$StagingRoot) {
    $pluginTarget = Join-Path $StagingRoot 'BepInEx\plugins\XingyiStarry.Mp'
    $configTarget = Join-Path $StagingRoot 'BepInEx\config'
    New-Item -ItemType Directory -Force -Path $pluginTarget, $configTarget | Out-Null
    Copy-Item -Path (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\XingyiStarry.Mp\*') -Destination $pluginTarget -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config\relay-default.cfg') -Destination (Join-Path $configTarget 'xingyistarry.mp.cfg')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'PACKAGE-README.txt') -Destination (Join-Path $StagingRoot 'XingyiStarry.Mp-README.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $StagingRoot 'XingyiStarry.Mp-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'THIRD-PARTY-NOTICES.md') -Destination (Join-Path $StagingRoot 'XingyiStarry.Mp-THIRD-PARTY-NOTICES.md')
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-XingyiStarry-MP.bat') -Destination $StagingRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-XingyiStarry-MP.ps1') -Destination $StagingRoot
    $licenseTarget = Join-Path $StagingRoot 'XingyiStarry.Mp-Licenses'
    New-Item -ItemType Directory -Force -Path $licenseTarget | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\Google.Protobuf-3.25.3-BSD-3-Clause.txt') -Destination $licenseTarget
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\DotNet-Runtime-MIT.txt') -Destination $licenseTarget
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'licenses\README.md') -Destination $licenseTarget
}

function New-Package([string]$Suffix, [bool]$IncludeBepInEx) {
    $staging = [IO.Path]::GetFullPath((Join-Path $outputRoot "staging-$version$Suffix"))
    Assert-SafeStagingPath $staging
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    if ($IncludeBepInEx) {
        Get-ChildItem -Force -LiteralPath $BepInExSource | Where-Object { $_.Name -ne '.package-sha256' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $staging -Recurse -Force
        }
        $licenseTarget = Join-Path $staging 'XingyiStarry.Mp-Licenses'
        New-Item -ItemType Directory -Force -Path $licenseTarget | Out-Null
        Copy-Item -Path (Join-Path $PSScriptRoot 'licenses\*') -Destination $licenseTarget -Force
    }
    Add-PluginFiles $staging

    $archive = Join-Path $outputRoot "XingyiStarry-MP-$version$Suffix.zip"
    if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
    Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
    Remove-Item -LiteralPath $staging -Recurse -Force
    $hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
    Write-Host "Package ready: $archive"
    Write-Host "SHA256: $($hash.Hash)"
}

New-Package -Suffix '' -IncludeBepInEx $false
New-Package -Suffix '-with-BepInEx' -IncludeBepInEx $true
