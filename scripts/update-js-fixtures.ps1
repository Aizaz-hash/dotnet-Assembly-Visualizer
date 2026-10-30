# Regenerates the analysis JSON the JavaScript tests read (tests\js\fixtures) from the real analyzer and the sample assemblies.
$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")
& "$PSScriptRoot\build-samples.ps1" | Out-Null
dotnet run -c Release --project tools/PerfPoc -- --runs 0 --json tests/js/fixtures `
  samples/circular-dependencies/bin/Release/net8.0/CircularDependencies.dll `
  samples/inheritance/bin/Release/net8.0/Inheritance.dll `
  samples/method-calls/bin/Release/net8.0/MethodCalls.dll
