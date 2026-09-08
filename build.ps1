param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'D:\Program Files\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
& $Dotnet build (Join-Path $projectRoot 'XingyiStarry.Mp.sln') -c $Configuration -p:UseSharedCompilation=false -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$package = Join-Path $projectRoot 'dist\BepInEx\plugins\XingyiStarry.Mp'
$patcherPackage = Join-Path $projectRoot 'dist\BepInEx\patchers'
New-Item -ItemType Directory -Force -Path $package, $patcherPackage | Out-Null
$pluginOutput = Join-Path $projectRoot "src\XingyiStarry.Mp\bin\$Configuration\netstandard2.1"
Copy-Item -Force -LiteralPath (Join-Path $pluginOutput 'XingyiStarry.Mp.dll') -Destination $package
Copy-Item -Force -LiteralPath (Join-Path $pluginOutput 'XingyiStarry.Mp.Protocol.dll') -Destination $package
Copy-Item -Force -LiteralPath (Join-Path $projectRoot "src\XingyiStarry.Mp.EarlyPatcher\bin\$Configuration\netstandard2.0\XingyiStarry.Mp.EarlyPatcher.dll") -Destination $patcherPackage
Write-Host "Package ready: $package"
