param(
    [Parameter(Mandatory = $true)]
    [string]$GameRoot,
    [Parameter(Mandatory = $true)]
    [string]$Tag,
    [string]$Token = '',
    [string]$OutputDirectory = '',
    [string]$Dotnet = 'dotnet',
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $projectRoot 'game-references.json'
$manifest = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json

if ($Tag -ne $manifest.tag) {
    throw "Tag '$Tag' does not match the pinned tag '$($manifest.tag)' in game-references.json. Update and review the manifest first."
}
if ($Tag -notmatch '^game-(?<version>[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?)$') {
    throw "Invalid game reference tag '$Tag'. Expected a value such as game-1.0.8."
}
if ($Matches.version -ne $manifest.version) {
    throw "Tag version '$($Matches.version)' does not match manifest version '$($manifest.version)'."
}

$managedSource = Join-Path ([IO.Path]::GetFullPath($GameRoot)) 'AnnW_Data\Managed'
foreach ($entry in $manifest.files) {
    $source = Join-Path $managedSource $entry.file
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Missing game assembly: $source" }
    $item = Get-Item -LiteralPath $source
    $actual = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($item.Length -ne [long]$entry.size -or $actual -ne $entry.sha256) {
        throw "Game assembly does not match the reviewed manifest: $($entry.file)"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $projectRoot '.deps\game-reference-packages'
}
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputRoot | Out-Null
$workRoot = Join-Path (Join-Path $projectRoot '.deps') ('game-refs-pack-' + [Guid]::NewGuid().ToString('N'))

try {
    $payloadRoot = Join-Path $workRoot 'refs'
    $payloadManaged = Join-Path $payloadRoot 'AnnW_Data\Managed'
    New-Item -ItemType Directory -Force -Path $payloadManaged | Out-Null
    foreach ($entry in $manifest.files) {
        Copy-Item -LiteralPath (Join-Path $managedSource $entry.file) -Destination $payloadManaged -Force
    }
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $payloadRoot 'refs.json') -Force

    $packProject = Join-Path $workRoot 'GameReferences.csproj'
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <PackageId>$($manifest.packageId)</PackageId>
    <Version>$($manifest.version)</Version>
    <Authors>XingyiStarry</Authors>
    <Description>Private compile-time references for Tactical Annihilation $($manifest.version).</Description>
    <PackageRequireLicenseAcceptance>false</PackageRequireLicenseAcceptance>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <SuppressDependenciesWhenPacking>true</SuppressDependenciesWhenPacking>
    <NoWarn>NU5100;NU5128</NoWarn>
  </PropertyGroup>
  <ItemGroup>
    <None Include="refs\**\*" Pack="true" PackagePath="refs\%(RecursiveDir)%(Filename)%(Extension)" />
  </ItemGroup>
</Project>
"@ | Set-Content -LiteralPath $packProject -Encoding utf8

    & $Dotnet pack $packProject -c Release -o $outputRoot -p:NuGetAudit=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed with exit code $LASTEXITCODE" }
    $package = Join-Path $outputRoot "$($manifest.packageId).$($manifest.version).nupkg"
    if (-not (Test-Path -LiteralPath $package -PathType Leaf)) { throw "Package was not created: $package" }
    Write-Host "Game reference package ready: $package"
    Write-Host "SHA256: $((Get-FileHash -LiteralPath $package -Algorithm SHA256).Hash)"

    if (-not $NoPush) {
        if ([string]::IsNullOrWhiteSpace($Token)) { $Token = $env:GH_PACKAGES_TOKEN }
        if ([string]::IsNullOrWhiteSpace($Token)) {
            $gh = Get-Command gh -ErrorAction SilentlyContinue
            if ($null -ne $gh) {
                $Token = (& $gh.Source auth token).Trim()
                if ($LASTEXITCODE -ne 0) { $Token = '' }
            }
        }
        if ([string]::IsNullOrWhiteSpace($Token)) {
            throw 'A GitHub token with write:packages is required. Pass -Token, set GH_PACKAGES_TOKEN, or log in with gh.'
        }
        & $Dotnet nuget push $package `
            --source $manifest.source `
            --api-key $Token `
            --skip-duplicate | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet nuget push failed with exit code $LASTEXITCODE" }
        Write-Host "Published private game reference package: $($manifest.packageId) $($manifest.version)"
    }
} finally {
    if (Test-Path -LiteralPath $workRoot) { Remove-Item -LiteralPath $workRoot -Recurse -Force }
}
