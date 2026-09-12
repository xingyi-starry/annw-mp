param(
    [string]$GameRoot = 'D:\code\annw-lan\Tactical Annihilation',
    [string]$BepInExSource = 'D:\code\annw-lan\AnnW.LanMp-0.18.0-with-BepInEx',
    [string]$ArtifactsPath = ''
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
$configTarget = Join-Path $GameRoot 'BepInEx\config'
New-Item -ItemType Directory -Force -Path $coreTarget, $pluginTarget, $debugToolsTarget, $patcherTarget, $configTarget | Out-Null
Get-ChildItem -File -LiteralPath (Join-Path $BepInExSource 'BepInEx\core') | ForEach-Object {
    $target = Join-Path $coreTarget $_.Name
    if (-not (Test-Path -LiteralPath $target)) { Copy-Item -LiteralPath $_.FullName -Destination $target }
}
$winhttpTarget = Join-Path $GameRoot 'winhttp.dll'
$doorstopTarget = Join-Path $GameRoot 'doorstop_config.ini'
if (-not (Test-Path -LiteralPath $winhttpTarget)) { Copy-Item -LiteralPath (Join-Path $BepInExSource 'winhttp.dll') -Destination $winhttpTarget }
if (-not (Test-Path -LiteralPath $doorstopTarget)) { Copy-Item -LiteralPath (Join-Path $BepInExSource 'doorstop_config.ini') -Destination $doorstopTarget }
Copy-Item -Force -Path (Join-Path $PSScriptRoot 'dist\BepInEx\plugins\XingyiStarry.Mp\*') -Destination $pluginTarget
$relayConfigTarget = Join-Path $configTarget 'xingyistarry.mp.cfg'
if (-not (Test-Path -LiteralPath $relayConfigTarget)) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'config\relay-default.cfg') -Destination $relayConfigTarget
} elseif (-not (Select-String -LiteralPath $relayConfigTarget -Pattern '^\s*\[Relay\]\s*$' -Quiet)) {
    Add-Content -LiteralPath $relayConfigTarget -Value "`r`n[Relay]`r`nEndpoint = 60.205.147.182:24555"
}
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $debugToolsSource = Join-Path $PSScriptRoot 'src\XingyiStarry.Mp.DebugTools\bin\Release\netstandard2.1\XingyiStarry.Mp.DebugTools.dll'
} else {
    $debugToolsSource = Join-Path $ArtifactsPath 'bin\XingyiStarry.Mp.DebugTools\release\XingyiStarry.Mp.DebugTools.dll'
}
Copy-Item -Force -LiteralPath $debugToolsSource -Destination $debugToolsTarget
$patcherFile = Join-Path $patcherTarget 'XingyiStarry.Mp.EarlyPatcher.dll'
Copy-Item -Force -LiteralPath (Join-Path $PSScriptRoot 'dist\BepInEx\patchers\XingyiStarry.Mp.EarlyPatcher.dll') -Destination $patcherFile
New-Item -ItemType File -Force -Path (Join-Path $GameRoot 'XingyiStarry.Mp.NoSteam') | Out-Null
Write-Host "Installed XingyiStarry MP to $pluginTarget"
Write-Host "Installed development DebugTools to $debugToolsTarget"
Write-Host "Installed development Steam bypass patch and marker to $GameRoot"
