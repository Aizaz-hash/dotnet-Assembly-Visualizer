#!/usr/bin/env bash
# Regenerates the analysis JSON the JavaScript tests read (tests/js/fixtures) from the real analyzer and the sample assemblies.
# Run it after changing a sample or the analyzer's output; an integration test fails when the checked-in fixtures are stale.
set -euo pipefail
cd "$(dirname "$0")/.."
bash scripts/build-samples.sh Release > /dev/null
dotnet run -c Release --project tools/PerfPoc -- --runs 0 --json tests/js/fixtures \
  samples/circular-dependencies/bin/Release/net8.0/CircularDependencies.dll \
  samples/inheritance/bin/Release/net8.0/Inheritance.dll \
  samples/method-calls/bin/Release/net8.0/MethodCalls.dll
