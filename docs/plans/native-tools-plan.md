# Plan: the `Dav` PowerShell module

Status: **approved scope, being built one commit at a time** on the branch `feat/dav-powershell-module`.

## 1. Scope (what was decided)

- **One tool: a PowerShell module named `Dav`.** There is no separate `dav` program and no `dotnet tool`.
- The cmdlets use the prefix `Dav` and run on **PowerShell 7 (Windows, Linux, macOS)** and on **Windows PowerShell 5.1** (.NET Framework 4.7.2 or newer).
- **Four checks only.** No C# decompiler, no general analyzer, no metrics:

  | Cmdlet | What it checks | Fails the command? |
  |---|---|---|
  | `Get-DavRiskyApi` | risky API calls | yes |
  | `Get-DavVulnerability` | known vulnerabilities in the dependencies | yes |
  | `Compare-DavVersion` | two builds of an assembly: public API changes and the version number | yes, on a version mismatch |
  | `Get-DavCircularDependency` | circular dependencies | **no, a warning only** |
  | `Invoke-DavCheck` | risky APIs, vulnerabilities and cycles on one assembly | yes, on risky APIs or vulnerabilities |

- Today's web-app command `scan` stays exactly as it is.

## 2. Where we are today (from the repository)

- The engine for all four checks exists inside **one ASP.NET project** (`net8.0`): `RiskyApiRules` (rule id, category, severity High/Low), the vulnerability scanner and its OSV / NuGet clients, `ApiDiffer` (breaking / compatible / info, and a suggested version bump), and `CircularDependencyDetectorService` on the call graph from `AssemblyAnalyzerService`.
- Only `scan` is reachable from a command line (`Services/Cli/CliScan.cs`, with `--fail-on`, exit codes 0/1/2/3, SARIF and SBOM output).
- There is **no** `CompareVersion` command and no hash comparison yet: both are built in this plan.

## 3. Behaviour

### 3.1 Failing, warning, and exit codes

- A failing check is a **terminating error** carrying the findings. `pwsh -File` / `pwsh -Command` then exits with **code 1**, so a CI step fails. The findings are still printed and are available on the error's `TargetObject`.
- Circular dependencies use `Write-Warning` and **never** change the result.
- A bad argument (unknown threshold, missing file, not a .NET assembly) is a parameter error: **non-zero exit**.
- An incomplete vulnerability lookup (no network) is **never** a clean pass: it is an error unless `-AllowIncomplete` is given, as with `scan` today.
- If several things happen at once: a bad argument wins; otherwise any failing check fails; warnings are shown in every case.

### 3.2 The risk threshold

`-FailOn` (alias `-RiskThreshold`) takes `low`, `medium` or `high`. A detected risk **at or above** the threshold fails the command:

| Detected risk | `low` | `medium` | `high` |
|---|---|---|---|
| Low | Fail | Pass | Pass |
| Medium | Fail | Fail | Pass |
| High | Fail | Fail | Fail |

- It applies to `Get-DavRiskyApi`, `Get-DavVulnerability`, `Compare-DavVersion` and `Invoke-DavCheck`, and reuses the severities that already exist (risky-API rules: High / Low; vulnerabilities: their own severities, mapped Critical to High). Detection code is not duplicated.
- Without the parameter, the defaults below apply; an unsupported value is rejected with a clear message.
- Cycles are **not** part of the threshold: they are informational and are never classified as a vulnerability.

### 3.3 Defaults

- `Get-DavRiskyApi`: fails on any lead.
- `Get-DavVulnerability`: confirmed and potential findings fail; informational ones (packages the runtime supplies) are shown and never fail.
- `Compare-DavVersion`: fails when the version bump is too small for the changes (see 3.4).
- A `dav.json` file in the repository lists findings accepted on purpose, each with a reason (`-Config`); `-Allow` / `-Ignore` do the same on the command line.

### 3.4 `Compare-DavVersion <old> <new>`

- Prints the changes (breaking, compatible, info), both versions and the bump the changes require. A **version mismatch** means the real bump is smaller than the required one (for example a breaking change without a major bump).
- **Fingerprints** (SHA-256, like an image digest) are shown with every comparison and never decide the result:
  - the **file hash** (identical bytes),
  - the **API fingerprint** (hash of the sorted public signatures; the same value means the same public API),
  - a **hash per method body**, to say which code changed.
- One-line verdict: *identical file*, *same public API, code changed*, or *public API changed*. The existing comparison logic is not redesigned.

### 3.5 `Get-DavCircularDependency` and the warning

Each distinct cycle is reported once (no duplicates), with the class names and the method names involved only, not the whole graph:

```text
[WARNING] Possible Circular Dependency Detected

Classes:
  - ClassA
  - ClassB
  - ClassC

Methods:
  - ClassA.Method1()
  - ClassB.Method2()
  - ClassC.Method3()

Warning: These classes and methods contain a possible circular dependency.
```

- With no cycles, nothing is printed.
- Same limits as the UI: calls through reflection, interfaces and delegates are not seen; direct recursion is not a cycle.
- `Invoke-DavCheck` shows the warnings and fails only because of risky APIs or vulnerabilities.

### 3.6 Result table

| Condition | Result |
|---|---|
| No qualifying risk | success |
| Risk below the threshold | success |
| Risk at or above the threshold | failure |
| Possible circular dependency only | success with a warning |
| Risk at or above the threshold and a circular dependency | failure with a warning |
| Invalid threshold | argument error, non-zero exit |

## 4. How to use it

```powershell
Import-Module ./Dav
Invoke-DavCheck .\bin\Release\net8.0\MyApp.dll -FailOn high
Compare-DavVersion .\old\MyLib.dll .\new\MyLib.dll -FailOn medium
Get-DavRiskyApi .\bin\Release\net8.0\MyApp.dll | Where-Object Severity -eq High
Get-DavCircularDependency .\bin\Release\net8.0\MyApp.dll
```

In CI: `pwsh -NoProfile -Command "Import-Module ./Dav; Invoke-DavCheck app.dll -FailOn high"`. GitHub-hosted runners already have `pwsh`. Users need PowerShell 7 on Linux and macOS; Windows has 5.1.

## 5. Architecture

1. **Extract the engine into a class library** `DotNetVisualizer.Core` (targets `netstandard2.0`, so it loads in .NET Framework 4.7.2+ and in current .NET): the assembly reader and call graph, the cycle detector, the risky-API rules, the API differ, the vulnerability scanner. **The decompiler stays in the web app.**
2. **The web app references Core** and behaves as before.
3. **A new binary-module project `Dav`** references Core only: one cmdlet class per cmdlet, shared parameters, one place for the threshold and the failure rules, a text output layer.
4. **Splitting `AssemblyAnalyzerService`** is the biggest piece: the call-graph and risky-hit parts move out with no change to what the web app returns. The existing C#, JS and UI tests prove nothing changed.
5. **Loading in PowerShell:** the module ships its own copies of the libraries it needs; on PowerShell 7 they load in an isolated context so they cannot clash with PowerShell's own versions.

## 6. Frameworks, release and signing

- Microsoft ends support of **.NET 8 and .NET 9 on 10 November 2026**; .NET 10 (LTS) is supported until 14 November 2028. The module therefore builds for **net10.0** (PowerShell 7) and **netstandard2.0** (Windows PowerShell 5.1); `Core` is `netstandard2.0`. No net6.0.
- Reading assemblies built for any .NET Framework, .NET Core or .NET 5+ is a test requirement.
- The web app moves to .NET 10 as a separate branch. A .NET 10 SDK is needed on the build machine.
- **Release:** one zip with the `Dav` folder for local use (`Import-Module`), plus a `SHA256SUMS` file; the **PowerShell Gallery** (`Install-Module Dav`) as the second channel. The Gallery needs its own account and a scoped API key that you create and store as a repository secret. Signing (Windows Authenticode, macOS) is postponed.

## 7. Phases (one commit at a time)

| Phase | Work | Done when |
|---|---|---|
| 0 | the plan (this document) | merged |
| 1 | **spike**: a throw-away `netstandard2.0` library + a cmdlet loaded in `pwsh` 7 and Windows PowerShell 5.1 with `System.Reflection.Metadata`; findings written down | go / no-go on the load strategy is recorded |
| 2 | **Core extraction**, in small commits (reader and call graph, cycle detector, risky rules, differ, vulnerability scanner) | all existing tests pass; web app and `scan` unchanged |
| 3 | module skeleton + `Get-DavCircularDependency` (warning, deduplicated) | tests incl. "warning, exit 0" |
| 4 | `Get-DavRiskyApi` + the threshold | threshold matrix tests |
| 5 | `Get-DavVulnerability` | offline / incomplete tests |
| 6 | `Compare-DavVersion` + fingerprints | existing comparison unchanged; bump and fingerprint tests |
| 7 | `Invoke-DavCheck`, `dav.json`, help and examples | the 13 behaviour tests below |
| 8 | CI on Windows, Linux, macOS and Windows PowerShell 5.1; release zip | green on all |

### Tests the behaviour must have

1. Low, medium and high threshold behaviour; 2. several risks of different severities; 3. exact threshold match; 4. risks below the threshold; 5. an invalid threshold; 6. no risk; 7. a cycle warning with correct class and method names; 8. several cycles without duplicates; 9. a cycle warning without failure; 10. threshold failure together with a cycle warning; 11. the process exit codes under `pwsh`; 12. existing comparison results unchanged; 13. the same results when `-FailOn` is omitted.

## 8. Risks

| Risk | What we do |
|---|---|
| Library version clashes inside PowerShell | phase 1 spike; isolated load context on PowerShell 7 |
| Splitting the analyzer changes the web app | mechanical moves, whole test suite and UI test after every commit |
| "Any finding fails" blocks teams on one unfixable item | `dav.json`, `-Allow`, `-Ignore` |
| Cycles are ignored because they only warn | they are printed in every run; never silent |
| Users without PowerShell 7 on Linux / macOS | documented install line; Windows needs nothing |
