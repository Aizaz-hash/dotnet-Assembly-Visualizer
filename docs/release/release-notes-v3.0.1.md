# dotnet Assembly Visualizer v3.0.1

New: **Dav**, a PowerShell module that runs the checks of the visualizer from the command line, in a build pipeline, without a browser. The web app itself behaves as in v3.0.0.

## The Dav PowerShell module

Dav reads the metadata and IL of a .NET assembly (the code is never run) and works in **PowerShell 7 on Windows, Linux and macOS** and in **Windows PowerShell 5.1**.

| Cmdlet | Checks | Fails the command? |
|---|---|---|
| `Get-DavRiskyApi` | calls to risky APIs (unsafe deserializers, process start, weak crypto, disabled TLS checks, native calls ...) | yes |
| `Get-DavVulnerability` | known vulnerabilities in the packages the assembly depends on | yes |
| `Compare-DavVersion` | API changes between two builds, and whether the version number covers them | yes, when the version is too small |
| `Get-DavCircularDependency` | circular call chains between methods | no, a warning only |
| `Invoke-DavCheck` | risky APIs, vulnerabilities and cycles of one assembly in one go | yes (risky APIs, vulnerabilities); cycles only warn |

- **A risk threshold.** `-FailOn low|medium|high` (alias `-RiskThreshold`): a risk at or above the level fails the command. Without it every risk fails.
- **Circular dependencies only warn.** Each cycle is reported once, with the class and method names; they never change the result.
- **Version comparison with fingerprints.** SHA-256 hashes of the file, of the public API and of each method body. A breaking change that is not covered by a major version bump fails the command.
- **Accept a finding on purpose** with `dav.json`, `-Allow` or `-Ignore`, each with a reason. An unfinished vulnerability lookup is never reported as a clean pass.
- **Made for CI.** A failed check is an error: exit code 1 under `pwsh -Command` or `$ErrorActionPreference = 'Stop'`, and `-ExitOnFailure` for scripts run with `pwsh -File`.
- **Get-Help** with examples for every cmdlet, and `about_Dav`.

```powershell
Expand-Archive Dav-3.0.1.zip -DestinationPath .
Import-Module ./Dav/Dav.psd1
Invoke-DavCheck .\bin\Release\net8.0\MyApp.dll -FailOn high
```

Files: `Dav-3.0.1.zip` (the module folder), `Dav.3.0.1.nupkg` (the same module as a NuGet package) and `SHA256SUMS`. The **container image** now also contains PowerShell 7.4 and the module (`docker run --rm -v "$PWD:/work" --entrypoint pwsh <image> -NoProfile -Command "Invoke-DavCheck /work/MyApp.dll"`). Guide: [docs/dav-module.md](../dav-module.md).

## Good to know

- Tested on Windows PowerShell 5.1 (.NET Framework 4.8), PowerShell 7.4 (.NET 8) and PowerShell 7.6 (.NET 10). Linux and macOS are covered by the CI job.
- Risky APIs are leads for a review, not proven vulnerabilities. Only package names and versions are sent for the vulnerability lookup (to OSV.dev and NuGet.org); `-SkipVulnerabilities` keeps everything on the machine.
- It reads metadata and IL: anything decided at run time (reflection, plugins) is invisible, and obfuscated assemblies give incomplete results.

## Under the hood

- The analysis engine moved into a class library, `DotNetVisualizer.Core` (the cycle detector, risky-API rules, API differ, vulnerability scanner and call graph). The web app and the `scan` command behave as before.
- The circular dependency detector no longer changes a dictionary while looping over its keys, which threw on .NET Framework.
