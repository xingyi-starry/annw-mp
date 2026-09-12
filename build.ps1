param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$Dotnet = 'D:\Program Files\dotnet\dotnet.exe',
    [string]$ArtifactsPath = ''
)

$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$buildArguments = @('build', (Join-Path $projectRoot 'XingyiStarry.Mp.sln'), '-c', $Configuration, '-p:UseSharedCompilation=false', '-p:NuGetAudit=false', '-nodeReuse:false')
if (-not [string]::IsNullOrWhiteSpace($ArtifactsPath)) { $buildArguments += "-p:ArtifactsPath=$ArtifactsPath" }
& $Dotnet @buildArguments
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }

$package = Join-Path $projectRoot 'dist\BepInEx\plugins\XingyiStarry.Mp'
$patcherPackage = Join-Path $projectRoot 'dist\BepInEx\patchers'
$configPackage = Join-Path $projectRoot 'dist\BepInEx\config'
New-Item -ItemType Directory -Force -Path $package, $patcherPackage, $configPackage | Out-Null
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $pluginOutput = Join-Path $projectRoot "src\XingyiStarry.Mp\bin\$Configuration\netstandard2.1"
    $patcherOutput = Join-Path $projectRoot "src\XingyiStarry.Mp.EarlyPatcher\bin\$Configuration\netstandard2.0\XingyiStarry.Mp.EarlyPatcher.dll"
} else {
    $configurationFolder = $Configuration.ToLowerInvariant()
    $pluginOutput = Join-Path $ArtifactsPath "bin\XingyiStarry.Mp\$configurationFolder"
    $patcherOutput = Join-Path $ArtifactsPath "bin\XingyiStarry.Mp.EarlyPatcher\$configurationFolder\XingyiStarry.Mp.EarlyPatcher.dll"
}
$runtimeFiles = @(
    'XingyiStarry.Mp.dll',
    'XingyiStarry.Mp.Protocol.dll',
    'Google.Protobuf.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll'
)
foreach ($file in $runtimeFiles) {
    Copy-Item -Force -LiteralPath (Join-Path $pluginOutput $file) -Destination $package
}
Copy-Item -Force -LiteralPath $patcherOutput -Destination $patcherPackage
Copy-Item -Force -LiteralPath (Join-Path $projectRoot 'config\relay-default.cfg') -Destination (Join-Path $configPackage 'xingyistarry.mp.cfg')
Write-Host "Package ready: $package"
