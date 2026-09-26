param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$BepInExSource = (Join-Path $PSScriptRoot '.deps\BepInEx_win_x64_5.4.23.5'),
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'),
    [string]$GameRoot = '',
    [string]$GameReferencesTag = '',
    [string]$GitHubToken = '',
    [string]$ArtifactsPath = ''
)

$ErrorActionPreference = 'Stop'

& (Join-Path $PSScriptRoot 'tools\Ensure-BepInEx.ps1') -Destination $BepInExSource
if (-not [string]::IsNullOrWhiteSpace($GameReferencesTag)) {
    if (-not [string]::IsNullOrWhiteSpace($GameRoot)) {
        throw 'Specify either -GameRoot or -GameReferencesTag, not both.'
    }
    $safeTag = $GameReferencesTag -replace '[^0-9A-Za-z._-]', '_'
    $GameRoot = Join-Path $PSScriptRoot ".deps\game-references\$safeTag"
    & (Join-Path $PSScriptRoot 'tools\Ensure-GameReferences.ps1') `
        -Tag $GameReferencesTag `
        -Destination $GameRoot `
        -Token $GitHubToken
}
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
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-XingyiStarry-MP.bat') -Destination $StagingRoot
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Uninstall-XingyiStarry-MP.ps1') -Destination $StagingRoot
}

function New-Package([string]$Suffix, [bool]$IncludeBepInEx) {
    $staging = [IO.Path]::GetFullPath((Join-Path $outputRoot "staging-$version$Suffix"))
    Assert-SafeStagingPath $staging
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null

    if ($IncludeBepInEx) {
        $coreTarget = Join-Path $staging 'BepInEx\core'
        New-Item -ItemType Directory -Force -Path $coreTarget | Out-Null
        Get-ChildItem -File -LiteralPath (Join-Path $BepInExSource 'BepInEx\core') -Filter '*.dll' | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $coreTarget -Force
        }
        Copy-Item -LiteralPath (Join-Path $BepInExSource 'doorstop_config.ini') -Destination $staging
        Copy-Item -LiteralPath (Join-Path $BepInExSource 'winhttp.dll') -Destination $staging
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
