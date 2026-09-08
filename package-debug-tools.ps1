param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'D:\Program Files\dotnet\dotnet.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases')
)

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'src\XingyiStarry.Mp.DebugTools\XingyiStarry.Mp.DebugTools.csproj'
& $Dotnet build $project -c $Configuration -p:UseSharedCompilation=false -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$source = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'src\XingyiStarry.Mp.DebugTools\DebugToolsPlugin.cs')
$match = [regex]::Match($source, 'PluginVersion\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'Could not determine Debug Tools PluginVersion.' }
$version = $match.Groups[1].Value

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$staging = [IO.Path]::GetFullPath((Join-Path $outputRoot "debug-tools-staging-$version"))
if (-not $staging.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe staging path: $staging"
}
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }

$pluginTarget = Join-Path $staging 'BepInEx\plugins\XingyiStarry.Mp.DebugTools'
New-Item -ItemType Directory -Force -Path $pluginTarget | Out-Null
$buildOutput = Join-Path $PSScriptRoot "src\XingyiStarry.Mp.DebugTools\bin\$Configuration\netstandard2.1\XingyiStarry.Mp.DebugTools.dll"
Copy-Item -LiteralPath $buildOutput -Destination $pluginTarget
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'DEBUG-TOOLS-README.txt') -Destination (Join-Path $staging 'XingyiStarry.Mp.DebugTools-README.txt')

$archive = Join-Path $outputRoot "XingyiStarry-MP-DebugTools-$version.zip"
if (Test-Path -LiteralPath $archive) { Remove-Item -LiteralPath $archive -Force }
Compress-Archive -Path (Join-Path $staging '*') -DestinationPath $archive -CompressionLevel Optimal
Remove-Item -LiteralPath $staging -Recurse -Force

$hash = Get-FileHash -LiteralPath $archive -Algorithm SHA256
Write-Host "Debug package ready: $archive"
Write-Host "SHA256: $($hash.Hash)"
