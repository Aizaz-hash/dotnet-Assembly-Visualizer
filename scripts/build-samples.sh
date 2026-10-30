#!/usr/bin/env bash
# Builds every sample assembly (samples/*/ ... *.csproj). Output: samples/<scenario>/bin/<Configuration>/net8.0/<Name>.dll
# usage: scripts/build-samples.sh [Release|Debug]
set -euo pipefail
cd "$(dirname "$0")/.."
config="${1:-Release}"
find samples -name '*.csproj' -not -path '*/obj/*' | sort | while read -r project; do
  echo "== $project"
  dotnet build "$project" -c "$config" --nologo -v q
done
echo
echo "Sample assemblies:"
find samples -path "*/bin/$config/net8.0/*.dll" \( -name 'Inheritance.dll' -o -name 'MethodCalls.dll' -o -name 'CircularDependencies.dll' -o -name 'RiskyApis.dll' -o -name 'VulnerableDemo.dll' -o -name 'ObfuscationLike.dll' -o -name 'MetricsDemo.dll' -o -name 'ApiCompat.dll' \) | sort
