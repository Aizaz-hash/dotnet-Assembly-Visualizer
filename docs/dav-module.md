# The `Dav` PowerShell module

Checks a .NET assembly **without running it**: risky API calls, known vulnerabilities in its dependencies, the API changes between two builds, and circular dependencies. It runs in **PowerShell 7** (Windows, Linux, macOS) and in **Windows PowerShell 5.1** (.NET Framework 4.7.2 or newer).

| Cmdlet | What it checks | Fails the command? |
|---|---|---|
| `Get-DavRiskyApi` | calls to risky APIs (unsafe deserializers, process start, weak crypto, disabled TLS checks, native calls ...) | **yes** |
| `Get-DavVulnerability` | known vulnerabilities in the packages the assembly depends on | **yes** |
| `Compare-DavVersion` | two builds of an assembly: API changes, version number, fingerprints | **yes**, when the version number is too small for the changes |
| `Get-DavCircularDependency` | circular call chains between methods | **no**, a warning only |
| `Invoke-DavCheck` | risky APIs, vulnerabilities and cycles of one assembly in one go | yes (risky APIs, vulnerabilities); cycles only warn |

Risky APIs are **leads for a review**, not proven vulnerabilities. A circular dependency is a design smell, not a vulnerability, so it never changes the result.

## Install

The module is a folder. Download `Dav-<version>.zip` from the release, unpack it, and import it:

```powershell
Expand-Archive Dav-0.1.0.zip -DestinationPath .
Import-Module ./Dav/Dav.psd1
Get-Command -Module Dav -Syntax        # every cmdlet with its parameters
```

To have it available in every session, put the `Dav` folder in one of the folders of `$env:PSModulePath` (for example `~/Documents/PowerShell/Modules` on Windows, `~/.local/share/powershell/Modules` on Linux and macOS).

`Install-Module Dav` (the PowerShell Gallery) works only after the module has been published there, which has not happened yet. The `Dav.<version>.nupkg` built by `scripts/build-module.ps1` is for a NuGet feed or a PowerShell repository you host yourself; it has not been installed through PowerShellGet in the project's own tests.

PowerShell 7 is a separate download on Linux and macOS (<https://learn.microsoft.com/powershell/scripting/install/installing-powershell>); Windows already ships Windows PowerShell 5.1.

## Quick start

```powershell
# everything on one assembly: fails on risky APIs or vulnerabilities, warns on cycles
Invoke-DavCheck .\bin\Release\net8.0\MyApp.dll

# only the risky API calls, high ones only
Get-DavRiskyApi .\MyApp.dll -FailOn high

# what changed between two builds, and does the version number cover it?
Compare-DavVersion .\1.2.0\MyLib.dll .\1.3.0\MyLib.dll

# circular dependencies (warnings; the command still succeeds)
Get-DavCircularDependency .\MyApp.dll

# the results are ordinary objects: filter, sort, export
Get-DavRiskyApi .\MyApp.dll -ErrorAction SilentlyContinue | Where-Object Severity -eq High | Select-Object RuleId, Api, Callers
```

## The risk threshold: when does a command fail?

`-FailOn` (alias `-RiskThreshold`) takes `low`, `medium` or `high`. A detected risk at **or above** the threshold fails the command.

| Detected risk | `-FailOn low` | `-FailOn medium` | `-FailOn high` |
|---|---|---|---|
| Low | fails | passes | passes |
| Medium | fails | fails | passes |
| High | fails | fails | fails |

- **Without `-FailOn` the threshold is `low`**: every risk fails the command.
- A value other than `low`, `medium` or `high` is rejected with an error that lists the allowed values.
- **Risky APIs** use the level of their rule (High, Medium or Low). **Vulnerabilities** use their severity: Critical counts as High, and Unknown as Medium. Vulnerabilities that only affect packages supplied by the .NET runtime (`informational`) are listed and never fail.
- **`Compare-DavVersion`**: the risk is a missing version bump. A breaking change without a *major* bump is **High**; new public API without a *minor* bump is **Medium**.
- **Circular dependencies are not part of the threshold.** They are warnings.

| Situation | Result |
|---|---|
| nothing found, or only risks below the threshold | success |
| a risk at or above the threshold | **failure** |
| only circular dependencies | success, with a warning |
| a risk at or above the threshold **and** circular dependencies | failure, and the warning is shown too |
| an invalid threshold, a missing file, a file that is not a .NET assembly | error |
| the vulnerability lookup could not finish (for example no network) | **failure** (`INCOMPLETE`), unless `-AllowIncomplete` |

## Using it in a script or in CI

A failed check is a **terminating error** that carries the findings in `$Error[0].TargetObject`. How that becomes a process exit code depends on how PowerShell is started:

| How you run it | A failed check gives |
|---|---|
| `pwsh -Command "Import-Module ./Dav; Invoke-DavCheck app.dll"` | exit code 1 |
| a script with `$ErrorActionPreference = 'Stop'` at the top (GitHub Actions' `shell: pwsh` does this for you) | exit code 1 |
| a script run with `pwsh -File` **without** `$ErrorActionPreference = 'Stop'` | **exit code 0**, unless you add `-ExitOnFailure` |

For that last case every cmdlet has **`-ExitOnFailure`**: it ends the PowerShell process with exit code 1 when the check fails (do not use it interactively, it closes your session). `-ExitOnFailure` is the safe choice for `pwsh -File`. A mistake in the arguments themselves (for example `-FailOn severe`) is reported by PowerShell before the cmdlet starts and follows PowerShell's own rules.

GitHub Actions:

```yaml
- name: Check the assembly
  shell: pwsh
  run: |
    Import-Module ./Dav/Dav.psd1
    Invoke-DavCheck ./bin/Release/net8.0/MyApp.dll -FailOn high
```

Azure DevOps / any CI with a plain command line:

```bash
pwsh -NoProfile -Command "Import-Module ./Dav/Dav.psd1; Invoke-DavCheck ./bin/Release/net8.0/MyApp.dll -FailOn high"
```

## Accepting a finding on purpose: `dav.json`

Findings your team has decided to accept are written down once, with a reason, in a `dav.json` file. `-Config <file>` names it; without that the module looks for `dav.json` in the **current directory**.

```json
{
  "risky": { "allow": [ { "id": "process-start", "reason": "starts our own signing tool with a fixed path" }, "native-call" ] },
  "vulns": { "ignore": [ { "id": "GHSA-xxxx-xxxx-xxxx", "reason": "not reachable, the vulnerable method is never called" } ] }
}
```

The same can be given on the command line: `-Allow process-start,native-call` and `-Ignore GHSA-xxxx-xxxx-xxxx`. Accepted findings are still listed (with `Allowed` / `Ignored` set to `True`) but never fail. A `dav.json` that cannot be read is an error, never a silent pass.

## Cmdlet reference

Every cmdlet takes the assembly as the first, positional parameter (`-Path`; it accepts several paths, pipeline input and `Get-ChildItem` results). All of them accept `-ExitOnFailure`.

### `Get-DavRiskyApi -Path <string[]> [-FailOn low|medium|high] [-Allow <string[]>] [-Config <file>]`
One object per kind of risky call: `RuleId`, `Severity`, `Category`, `Title`, `Api`, `Count`, `Callers`, `Why`, `Advice`, `Allowed`. Most severe first.

### `Get-DavVulnerability -Path <string[]> [-FailOn ...] [-Manifest <file>] [-MaxLevel 1-4] [-Ignore <string[]>] [-AllowIncomplete] [-Config <file>]`
One object per finding: `Id`, `Severity`, `Package`, `Version`, `Confidence` (`confirmed`, `potential` or `informational`), `FixedIn`, `Summary`, `Level`, `DependencyPath`, `Url`, `Ignored`. Exact package versions come from the `.deps.json` next to the assembly (or `-Manifest`: `.deps.json`, `project.assets.json` or `packages.lock.json`); without a manifest the versions are estimated, and such findings are `potential`. **Only package names and versions leave the machine** (to OSV.dev and NuGet.org); your code is never sent. The addresses can be replaced with the environment variables `VULN_OSV_URL` and `VULN_NUGET_URL` (for a mirror). `-Verbose` shows the notes of the scan.

### `Compare-DavVersion -OldPath <file> -NewPath <file> [-FailOn ...]`
One object with the changes (`Changes`, `Breaking`, `Compatible`, `Info`), both versions, `RequiredBump` (what the changes need: major, minor or patch), `ActualBump` (what the version numbers did, `none` when the same or lower), `VersionMismatch`, `MismatchLevel` and the fingerprints:

- `OldFileHash` / `NewFileHash`: SHA-256 of the files. Equal means the very same file.
- `OldApiFingerprint` / `NewApiFingerprint`: SHA-256 of the sorted public signatures. Equal means the same public API, whatever happened inside the methods.
- `ChangedMethods`, `AddedMethods`, `RemovedMethods`: the methods whose code changed, found by hashing each method body (tokens are replaced by the names they point to, so adding an unrelated member does not mark every method as changed).
- `Verdict`: `identical file`, `same public API, code changed` or `public API changed`.

The hashes only inform; the version rule decides pass or fail.

### `Get-DavCircularDependency -Path <string[]>`
One object per distinct cycle: `Assembly`, `Classes`, `Methods`. Each cycle is reported once, even when it can be entered from several methods, and also written as a warning:

```text
[WARNING] Possible Circular Dependency Detected

Classes:
  - Samples.Cycles.Left
  - Samples.Cycles.Middle
  - Samples.Cycles.Right

Methods:
  - Samples.Cycles.Left.Step(int n, Samples.Cycles.Middle next)
  - Samples.Cycles.Middle.Step(int n, Samples.Cycles.Right next, Samples.Cycles.Left back)
  - Samples.Cycles.Right.Step(int n, Samples.Cycles.Left next, Samples.Cycles.Middle back)

Warning: These classes and methods contain a possible circular dependency.
```

A method calling itself is not reported. Calls through reflection, interfaces and delegates are not seen, so a cycle that only exists that way is not found. With no cycles nothing is printed.

### `Invoke-DavCheck -Path <string[]> [-FailOn ...] [-Allow ...] [-Ignore ...] [-Manifest <file>] [-AllowIncomplete] [-SkipVulnerabilities] [-Config <file>]`
One summary object per assembly: `RiskyApis`, `Vulnerabilities`, `CircularDependencies`, `VulnerabilityCheckComplete`, `Passed`, `Reasons`. `-SkipVulnerabilities` keeps everything on the machine.

## What it cannot tell you

- It reads metadata and IL; it does not run the code. Anything decided at run time (reflection, dynamic loading, plugins) is invisible.
- Obfuscated assemblies hide names and calls; the results are then incomplete.
- A risky API call is a lead. Most calls are fine; the point is that someone looked.
- `Compare-DavVersion` judges the public API from metadata. A behaviour change inside a method is not an API change; use the method hashes to see which code changed.
- Without an exact package list (`.deps.json`) the vulnerability scan estimates versions and says so (`potential`).

## Build it yourself

```powershell
./scripts/build-module.ps1                    # artifacts/Dav, Dav-<version>.zip, Dav.<version>.nupkg, SHA256SUMS
./scripts/build-module.ps1 -Version 1.0.0     # the same with another version number
```

The script publishes nothing. Publishing to the PowerShell Gallery or to a NuGet feed is a deliberate step with your own API key:

```powershell
Publish-Module -Path ./artifacts/Dav -NuGetApiKey $env:PSGALLERY_KEY      # PowerShell Gallery
dotnet nuget push ./artifacts/Dav.1.0.0.nupkg --source https://api.nuget.org/v3/index.json --api-key $env:NUGET_KEY   # a NuGet feed
```

Tests: `dotnet test` runs the C# tests (the rules behind the cmdlets). `pwsh -File tests/ps/Dav.Tests.ps1 -Module artifacts/Dav -Samples <folder with the sample assemblies>` runs 33 end-to-end checks on the real process exit codes; run it with `powershell.exe` too to test Windows PowerShell 5.1.
