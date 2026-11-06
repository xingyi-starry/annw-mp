param(
    [string]$GameRoot = 'D:\code\annw-lan\Tactical Annihilation',
    [string]$BepInExSource = (Join-Path $PSScriptRoot '.deps\BepInEx_win_x64_5.4.23.5'),
    [string]$ArtifactsPath = '',
    [string]$RelayEndpoint = '127.0.0.1:24555'
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'tools\Ensure-BepInEx.ps1') -Destination $BepInExSource
$required = @('AnnW.exe', 'AnnW_Data\Managed\Assembly-CSharp.dll')
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $GameRoot $relative))) { throw "Invalid game directory: missing $relative" }
}

$coreTarget = Join-Path $GameRoot 'BepInEx\core'
$pluginTarget = Join-Path $GameRoot 'BepInEx\plugins\XingyiStarry.Mp'
$debugToolsTarget = Join-Path $GameRoot 'BepInEx\plugins\XingyiStarry.Mp.DebugTools'
$configTarget = Join-Path $GameRoot 'BepInEx\config'
New-Item -ItemType Directory -Force -Path $coreTarget, $pluginTarget, $debugToolsTarget, $configTarget | Out-Null
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
    # Development hosting and the local relay must listen on different ports.
    [IO.File]::WriteAllText($relayConfigTarget, "[Network]`r`nPort = 24556`r`n", [Text.UTF8Encoding]::new($false))
}
$relayConfigText = [IO.File]::ReadAllText($relayConfigTarget)
$relaySection = [regex]::Match($relayConfigText, '(?ms)^\[Relay\][^\r\n]*\r?\n.*?(?=^\[|\z)')
if ($relaySection.Success) {
    $sectionText = $relaySection.Value
    if ($sectionText -match '(?m)^\s*Endpoint\s*=') {
        $sectionText = [regex]::Replace($sectionText, '(?m)^\s*Endpoint\s*=[^\r\n]*', [System.Text.RegularExpressions.MatchEvaluator]{ param($match) "Endpoint = $RelayEndpoint" })
    } else { $sectionText += "`r`nEndpoint = $RelayEndpoint`r`n" }
    $relayConfigText = $relayConfigText.Remove($relaySection.Index, $relaySection.Length).Insert($relaySection.Index, $sectionText)
} else { $relayConfigText += "`r`n[Relay]`r`nEndpoint = $RelayEndpoint`r`n" }
[IO.File]::WriteAllText($relayConfigTarget, $relayConfigText, [Text.UTF8Encoding]::new($false))
if ([string]::IsNullOrWhiteSpace($ArtifactsPath)) {
    $debugToolsSource = Join-Path $PSScriptRoot 'src\XingyiStarry.Mp.DebugTools\bin\Release\netstandard2.1\XingyiStarry.Mp.DebugTools.dll'
} else {
    $debugToolsSource = Join-Path $ArtifactsPath 'bin\XingyiStarry.Mp.DebugTools\release\XingyiStarry.Mp.DebugTools.dll'
}
Copy-Item -Force -LiteralPath $debugToolsSource -Destination $debugToolsTarget
Write-Host "Installed XingyiStarry MP to $pluginTarget"
Write-Host "Installed development DebugTools to $debugToolsTarget"
Write-Host "Development relay endpoint: $RelayEndpoint"
