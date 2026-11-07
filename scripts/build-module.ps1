<#
.SYNOPSIS
    Builds the Dav PowerShell module: the module folder, a zip and a NuGet package. Publishes nothing.

.DESCRIPTION
    1. publishes src/Dav (netstandard2.0, one build for PowerShell 7 and Windows PowerShell 5.1),
    2. assembles artifacts/Dav (Dav.dll, the libraries it carries, Dav.psd1 and the help),
    3. writes artifacts/Dav-<version>.zip and artifacts/Dav.<version>.nupkg, plus SHA256SUMS.

    Publishing is a separate, deliberate step with your own key (see docs/dav-module.md, "Publishing").

.EXAMPLE
    ./scripts/build-module.ps1
    ./scripts/build-module.ps1 -Version 1.0.0 -Output ./out
#>
[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Output,   # default: artifacts at the repository root
    # where the pictures of the guide are published: nuget.org and the PowerShell Gallery only show images from a web address
    [string] $ImageBase = 'https://raw.githubusercontent.com/Aizaz-hash/dotnet-Assembly-Visualizer/v3/docs/images/'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$manifestSource = Join-Path $root 'src/Dav/Dav.psd1'
if (-not $Output) { $Output = Join-Path $root 'artifacts' }

if (-not $Version) {
    $Version = (Import-PowerShellDataFile $manifestSource).ModuleVersion
}
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "Version must look like 1.2.3 (got '$Version')." }

New-Item -ItemType Directory -Force $Output | Out-Null
$Output = Resolve-Path $Output
$staging = Join-Path $Output 'staging'
$module = Join-Path $Output 'Dav'
Remove-Item $staging, $module -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Building Dav $Version ($Configuration)..."
dotnet publish (Join-Path $root 'src/Dav/Dav.csproj') -c $Configuration -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

New-Item -ItemType Directory -Force $module | Out-Null
# the libraries the module carries; the PowerShell reference assembly and the debug symbols stay out
Get-ChildItem $staging -Filter *.dll | Where-Object Name -ne 'System.Management.Automation.dll' | Copy-Item -Destination $module
Copy-Item (Join-Path $staging 'en-US') (Join-Path $module 'en-US') -Recurse   # Get-Help

$manifest = (Get-Content $manifestSource -Raw) -replace "ModuleVersion\s*=\s*'[^']*'", "ModuleVersion         = '$Version'"
Set-Content -Path (Join-Path $module 'Dav.psd1') -Value $manifest -Encoding UTF8

$help = Join-Path $root 'docs/dav-module.md'
# the README of the module and of the NuGet package (shown on nuget.org): the guide, with its pictures as web addresses
if (Test-Path $help) {
    $readme = (Get-Content $help -Raw).Replace('](images/', '](' + $ImageBase)
    Set-Content -Path (Join-Path $module 'README.md') -Value $readme -Encoding UTF8
}
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $module 'LICENSE') -ErrorAction SilentlyContinue

# the finished folder must be a valid module
Test-ModuleManifest (Join-Path $module 'Dav.psd1') | Out-Null

$zip = Join-Path $Output "Dav-$Version.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path $module -DestinationPath $zip

Write-Host 'Packing the NuGet package...'
dotnet pack (Join-Path $root 'packaging/Dav.Package.csproj') -c $Configuration -o $Output --nologo -v q "-p:ModuleDir=$module" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }
$nupkg = Join-Path $Output "Dav.$Version.nupkg"

Remove-Item $staging -Recurse -Force
$sums = foreach ($f in $zip, $nupkg) { '{0}  {1}' -f (Get-FileHash $f -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $f -Leaf) }
Set-Content -Path (Join-Path $Output 'SHA256SUMS') -Value $sums -Encoding ASCII

Write-Host ''
Write-Host "Module folder : $module"
Write-Host "Zip           : $zip"
Write-Host "NuGet package : $nupkg"
$sums | ForEach-Object { Write-Host "  $_" }
