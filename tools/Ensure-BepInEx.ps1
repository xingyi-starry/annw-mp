param(
    [string]$Destination = (Join-Path $PSScriptRoot '..\.deps\BepInEx_win_x64_5.4.23.5')
)

$ErrorActionPreference = 'Stop'

$version = '5.4.23.5'
$assetName = "BepInEx_win_x64_$version.zip"
$downloadUri = "https://github.com/BepInEx/BepInEx/releases/download/v$version/$assetName"
$expectedSha256 = '82F9878551030F54657792C0740D9D51A09500EEAE1FBA21106B0C441E6732C4'
$expectedCoreSha256 = '8255B28902886085C578B9E427D3073C97002DB85176D2090CDEDA90EF14CE70'
$expectedDoorstopSha256 = '8C6CDBC38836DEE87E3368F5DE1994D7C0CCEBF29E4CE7ABA3C0981F9375412C'
$destinationPath = [IO.Path]::GetFullPath($Destination)
$requiredFile = Join-Path $destinationPath 'BepInEx\core\BepInEx.dll'
$doorstopFile = Join-Path $destinationPath 'winhttp.dll'
$markerFile = Join-Path $destinationPath '.package-sha256'

if ((Test-Path -LiteralPath $requiredFile) -and (Test-Path -LiteralPath $doorstopFile) -and (Test-Path -LiteralPath $markerFile)) {
    $installedHash = (Get-Content -Raw -LiteralPath $markerFile).Trim()
    $coreHash = (Get-FileHash -LiteralPath $requiredFile -Algorithm SHA256).Hash
    $doorstopHash = (Get-FileHash -LiteralPath $doorstopFile -Algorithm SHA256).Hash
    if (($installedHash -eq $expectedSha256) -and ($coreHash -eq $expectedCoreSha256) -and ($doorstopHash -eq $expectedDoorstopSha256)) {
        Write-Host "BepInEx $version is ready: $destinationPath"
        return
    }
}

$dependencyRoot = Split-Path -Parent $destinationPath
$downloadRoot = Join-Path $dependencyRoot 'downloads'
$archive = Join-Path $downloadRoot $assetName
$extractRoot = Join-Path $dependencyRoot "extract-$version"
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null

if (-not (Test-Path -LiteralPath $archive)) {
    Write-Host "Downloading official BepInEx $version package..."
    Invoke-WebRequest -Uri $downloadUri -OutFile $archive
}

$actualSha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash
if ($actualSha256 -ne $expectedSha256) {
    throw "BepInEx package SHA-256 mismatch. Expected $expectedSha256, got $actualSha256."
}

if (Test-Path -LiteralPath $extractRoot) {
    Remove-Item -LiteralPath $extractRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $extractRoot | Out-Null
Expand-Archive -LiteralPath $archive -DestinationPath $extractRoot -Force
if (-not (Test-Path -LiteralPath (Join-Path $extractRoot 'BepInEx\core\BepInEx.dll'))) {
    throw 'The official BepInEx archive does not contain the expected core assembly.'
}

if (Test-Path -LiteralPath $destinationPath) {
    Remove-Item -LiteralPath $destinationPath -Recurse -Force
}
Move-Item -LiteralPath $extractRoot -Destination $destinationPath
Set-Content -LiteralPath $markerFile -Value $expectedSha256 -Encoding ASCII
Write-Host "BepInEx $version is ready: $destinationPath"
