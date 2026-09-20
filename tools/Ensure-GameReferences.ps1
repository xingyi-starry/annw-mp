param(
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Destination = '',
    [string]$Token = '',
    [string]$Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $projectRoot 'game-references.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if ($Tag -ne $manifest.tag) {
    throw "Game reference tag '$Tag' is not pinned by game-references.json (expected '$($manifest.tag)')."
}

if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $projectRoot ".deps\game-references\$Tag"
}
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$destinationParent = Split-Path -Parent $destinationRoot
if ([string]::IsNullOrWhiteSpace($destinationParent) -or $destinationRoot -eq [IO.Path]::GetPathRoot($destinationRoot)) {
    throw "Unsafe game reference destination: $destinationRoot"
}

function Test-ReferenceSet([string]$Root) {
    $managed = Join-Path $Root 'AnnW_Data\Managed'
    foreach ($entry in $manifest.files) {
        $file = Join-Path $managed $entry.file
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $false }
        $item = Get-Item -LiteralPath $file
        if ($item.Length -ne [long]$entry.size) { return $false }
        $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $entry.sha256) { return $false }
    }
    return $true
}

if ((Test-Path -LiteralPath $destinationRoot) -and (Test-ReferenceSet $destinationRoot)) {
    Write-Host "Game references ready: $destinationRoot ($Tag)"
    return
}

if ([string]::IsNullOrWhiteSpace($Token)) { $Token = $env:GH_PACKAGES_TOKEN }
if ([string]::IsNullOrWhiteSpace($Token)) { $Token = $env:GITHUB_TOKEN }
if ([string]::IsNullOrWhiteSpace($Token)) {
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    if ($null -ne $gh) {
        $Token = (& $gh.Source auth token).Trim()
        if ($LASTEXITCODE -ne 0) { $Token = '' }
    }
}
if ([string]::IsNullOrWhiteSpace($Token)) {
    throw 'A GitHub token with read:packages is required. Pass -Token, set GH_PACKAGES_TOKEN, or log in with gh.'
}

New-Item -ItemType Directory -Force -Path $destinationParent | Out-Null
$workRoot = Join-Path $destinationParent ('.restore-' + [Guid]::NewGuid().ToString('N'))
$stagingRoot = Join-Path $destinationParent ('.staging-' + [Guid]::NewGuid().ToString('N'))

try {
    New-Item -ItemType Directory -Force -Path $workRoot, $stagingRoot | Out-Null
    $configPath = Join-Path $workRoot 'NuGet.Config'
    @'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
  </packageSources>
</configuration>
'@ | Set-Content -LiteralPath $configPath -Encoding utf8

    & $Dotnet nuget add source $manifest.source `
        --name github `
        --username $manifest.owner `
        --password $Token `
        --store-password-in-clear-text `
        --configfile $configPath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet nuget add source failed with exit code $LASTEXITCODE" }

    $restoreProject = Join-Path $workRoot 'Restore.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup>
  <ItemGroup>
    <PackageReference Include="$($manifest.packageId)" Version="[$($manifest.version)]" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $restoreProject -Encoding utf8

    $packagesRoot = Join-Path $workRoot 'packages'
    & $Dotnet restore $restoreProject `
        --configfile $configPath `
        --packages $packagesRoot `
        --no-cache `
        --force-evaluate `
        -p:NuGetAudit=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Game reference restore failed with exit code $LASTEXITCODE" }

    $packageRoot = Join-Path $packagesRoot (([string]$manifest.packageId).ToLowerInvariant())
    $packageRoot = Join-Path $packageRoot (([string]$manifest.version).ToLowerInvariant())
    $referenceRoot = Join-Path $packageRoot 'refs'
    if (-not (Test-Path -LiteralPath (Join-Path $referenceRoot 'AnnW_Data\Managed'))) {
        throw "The restored package does not contain refs/AnnW_Data/Managed: $packageRoot"
    }
    Copy-Item -LiteralPath (Join-Path $referenceRoot 'AnnW_Data') -Destination $stagingRoot -Recurse -Force
    Copy-Item -LiteralPath (Join-Path $referenceRoot 'refs.json') -Destination (Join-Path $stagingRoot '.game-references.json') -Force

    if (-not (Test-ReferenceSet $stagingRoot)) {
        throw "The restored package '$($manifest.packageId) $($manifest.version)' failed file hash validation."
    }

    if (Test-Path -LiteralPath $destinationRoot) {
        $marker = Join-Path $destinationRoot '.game-references.json'
        if (-not (Test-Path -LiteralPath $marker -PathType Leaf)) {
            throw "Refusing to replace an unmanaged directory: $destinationRoot"
        }
        Remove-Item -LiteralPath $destinationRoot -Recurse -Force
    }
    Move-Item -LiteralPath $stagingRoot -Destination $destinationRoot
    Write-Host "Game references restored and verified: $destinationRoot ($Tag)"
} finally {
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
    if (Test-Path -LiteralPath $stagingRoot) { Remove-Item -LiteralPath $stagingRoot -Recurse -Force }
}
