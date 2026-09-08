param(
    [string]$GameRoot = 'D:\code\annw-lan\Tactical Annihilation',
    [string]$BepInExSource = 'D:\code\annw-lan\AnnW.LanMp-0.18.0-with-BepInEx',
    [switch]$NoSteam
)

$ErrorActionPreference = 'Stop'
$required = @('AnnW.exe', 'AnnW_Data\Managed\Assembly-CSharp.dll')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $GameRoot $relative))) { throw "Invalid game directory: missing $relative" }
}

$coreTarget = Join-Path $GameRoot 'BepInEx\core'
$pluginTarget = Join-Path $GameRoot 'BepInEx\plugins\XingyiStarry.Mp'
$debugToolsTarget = Join-Path $GameRoot 'BepInEx\plugins\XingyiStarry.Mp.DebugTools'
$patcherTarget = Join-Path $GameRoot 'BepInEx\patchers'
New-Item -ItemType Directory -Force -Path $coreTarget, $pluginTarget, $debugToolsTarget, $patcherTarget | Out-Null
Get-ChildItem -File -LiteralPath (Join-Path $BepInExSource 'BepInEx\core') | ForEach-Object {
    $target = Join-Path $coreTarget $_.Name
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
}
$winhttpTarget = Join-Path $GameRoot 'winhttp.dll'
$doorstopTarget = Join-Path $GameRoot 'doorstop_config.ini'
if (-not (Test-Path -LiteralPath $winhttpTarget)) { Copy-Item -LiteralPath (Join-Path $BepInExSource 'winhttp.dll') -Destination $winhttpTarget }
if (-not (Test-Path -LiteralPath $doorstopTarget)) { Copy-Item -LiteralPath (Join-Path $BepInExSource 'doorstop_config.ini') -Destination $doorstopTarget }
Copy-Item -Force -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\XingyiStarry.Mp\XingyiStarry.Mp.dll') -Destination $pluginTarget
Copy-Item -Force -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\XingyiStarry.Mp\XingyiStarry.Mp.Protocol.dll') -Destination $pluginTarget
Copy-Item -Force -LiteralPath (Join-Path $PSScriptRoot 'src\XingyiStarry.Mp.DebugTools\bin\Release\netstandard2.1\XingyiStarry.Mp.DebugTools.dll') -Destination $debugToolsTarget
$patcherFile = Join-Path $patcherTarget 'XingyiStarry.Mp.EarlyPatcher.dll'
if (-not (Test-Path -LiteralPath $patcherFile)) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\patchers\XingyiStarry.Mp.EarlyPatcher.dll') -Destination $patcherFile }
if ($NoSteam) { New-Item -ItemType File -Force -Path (Join-Path $GameRoot 'XingyiStarry.Mp.NoSteam') | Out-Null }
Write-Host "Installed XingyiStarry MP to $pluginTarget"
Write-Host "Installed development DebugTools to $debugToolsTarget"
