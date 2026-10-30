# Builds every sample assembly (samples\*\ ... *.csproj). Output: samples\<scenario>\bin\<Configuration>\net8.0\<Name>.dll
# usage: .\scripts\build-samples.ps1 [-Configuration Release]
param([string]$Configuration = "Release")
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
Get-ChildItem samples -Recurse -Filter *.csproj | Where-Object { $_.FullName -notmatch "\\obj\\" } | Sort-Object FullName | ForEach-Object {
    Write-Host "== $($_.FullName)"
    dotnet build $_.FullName -c $Configuration --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed: $($_.Name)" }
}
Write-Host ""
Write-Host "Sample assemblies:"
$names = "Inheritance","MethodCalls","CircularDependencies","RiskyApis","VulnerableDemo","ObfuscationLike","MetricsDemo","ApiCompat"
Get-ChildItem samples -Recurse -Filter *.dll | Where-Object { $_.FullName -match "\\bin\\$Configuration\\net8.0\\" -and $names -contains $_.BaseName } | ForEach-Object { $_.FullName }
