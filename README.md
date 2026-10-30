# .NET Assembly Visualizer

Explore the architecture of a .NET assembly in seconds - inheritance graphs, method call graphs, circular call chains and known vulnerabilities in its dependencies - without loading or running the assembly.

I built this tool because I was tired of staring at massive, unreadable dependency graphs when trying to understand how the parts of a complex .NET assembly talk to each other. It reads the DLL binary directly (`System.Reflection.Metadata` plus IL scanning), so there is no runtime overhead and no slow loading.

![Method calls with circular dependency markers](docs/images/v2-calls.png)

*The screenshots on this page show a small sample assembly.*

---

## Contents

- [Features](#features)
- [Quick start](#quick-start)
- [Using the UI](#using-the-ui)
- [Configuration](#configuration)
- [How it works](#how-it-works)
- [Project structure](#project-structure)
- [Releasing](#releasing)
- [License](#license)
- [Contributing and contact](#contributing-and-contact)

---

## Features

### Assembly explorer and inheritance view

An ILSpy-style explorer on the left (assembly → namespaces → types → base types, derived types and methods) with search and Back / Forward navigation. The **Inheritance** tab draws the selected type's base types, interfaces and derived types as an interactive **graph** (bases above, derived types below, dashed lines for interface implementations). Use *Depth* to control how many levels are shown, double-click a type to centre the graph on it, and use *Fit* or *PNG* to zoom or export. A **Tree** toggle switches to an expandable list of the same relationships if you prefer it.

![Inheritance graph for a type](docs/images/v2-inheritance.png)

The **Whole assembly** mode draws every type hierarchy of the assembly in one graph (capped at 300 types for readability; the tree view lists all of them and expands lazily).

![Whole-assembly inheritance graph](docs/images/v2-all.png)

### Method calls

Pick a method to see its callers and callees (depth 1-4). Each method appears **once**, however many paths lead to it; repeated calls are merged into a single `×N` edge. Double-click a node to re-centre the graph on it.

### Circular dependency detection

Methods that take part in a circular call chain are marked in the explorer and get a clickable **⚠ Circular Dependency Detected** marker above them in the call graph (see the first screenshot). Clicking it opens the dedicated view: every distinct chain through that method, e.g. `A() → B() → C() → A()`, with the start and the loop-back marked. Click any method in a chain to jump back to its call graph.

![Circular dependency view](docs/images/v2-cycles.png)

### Vulnerabilities Scan

The **Vulnerabilities** view checks the dependencies of the assembly you have loaded for known security vulnerabilities and puts **Critical and High** findings first. It looks up the *exact* package versions in [OSV.dev](https://osv.dev) (GitHub Security Advisories, NVD and others) - nothing is executed, installed or modified.

![Vulnerability scan with a Critical finding selected](docs/images/v2-vuln-main.png)

- **What is scanned:** the dependencies of the loaded assembly. With no extra file the scan reads the assembly's own references and follows the packages' dependencies (see *No manifest needed* below); a `.deps.json`, `project.assets.json` or `packages.lock.json` (*Add manifest…*, or open the folder it sits in) makes the versions exact. You can also tick *Also scan this tool's own packages* to check the packages this application itself ships with (not your assembly). The scan needs a loaded assembly: its result belongs to that assembly, is kept while you switch between assemblies, and disappears when you remove it. An assembly that only references the .NET runtime has no NuGet packages to check, and the view says exactly that instead of "database unreachable".
- **Automatic, in the background:** the first time you open an assembly, a small notification asks once whether dependencies may be checked automatically (only package names and versions are sent). After that, every assembly you open or select is scanned in the background - nothing blocks, the **Vulnerabilities Scan** tab shows a spinning ring while it runs and a count when it is done, and a short toast reports the result. A finished scan is reused for an hour while the file and its manifest are unchanged (no repeated lookups when you click between assemblies); a changed file, a newly found `.deps.json` or **Re-run scan** scans again, and a failed scan is not retried for two minutes. Switch it off with the **Scan automatically** checkbox in that view; manual scans keep working.
- **What you get for every finding:** CVE / GHSA identifier, CVSS score and severity (computed from the CVSS v3 vector, or the advisory's own rating when only a v4 vector is published), affected component and version, the weakness (CWE), potential impact and exploitation conditions derived from the vector, fixed versions, a recommended upgrade with a command *for you to run*, the advisory text and references.
- **Summary and filters:** counts per severity (click a card to filter), filters for severity, component, status and confidence, plus text search. Statuses are **Open**, **Accepted Risk** and **Fixed**; a finding you mark as fixed re-opens automatically if the next scan still reports it. Reports can be exported as **PDF** (the browser's print dialog - choose *Save as PDF*) or as JSON.
- **Confirmed, potential, informational:** every finding is verified against the advisory's affected-version ranges. It is **confirmed** when the exact version is known (from a manifest) and lies inside an affected range. It is **potential** when that cannot be established - the version was estimated from an assembly reference, or it was not found inside the advisory's listed ranges. It is **informational** when the package is one the .NET runtime supplies: whether the vulnerable code runs depends on the runtime's patch level, which a scan cannot see, so it is shown for awareness and is never counted by the command line's `--fail-on`. Heuristic findings are never presented as confirmed.

![A potential finding explained](docs/images/v2-vuln-potential.png)

- **Honest about gaps:** if the database cannot be reached, a component fails, or an advisory cannot be downloaded, the report says the scan is **incomplete** instead of showing "no vulnerabilities". The .NET runtime / base image itself is not scanned.
- **No manifest needed:** the scan starts from the loaded assembly's own references (level 1), matches them to NuGet packages, then follows each package's declared dependencies down to **level 4** (level 0 is the assembly itself). Every dependency is listed once with its level, where its version came from (exact from a `.deps.json`, estimated from the assembly version, or the lowest version the parent accepts) and a plain status: *Assembly discovered*, *Package identified*, *Version identified*, *Vulnerability found*, *Vulnerability lookup unavailable* or *Dependency could not be resolved*. Findings with an estimated version are marked *potential*; adding the `.deps.json` makes versions exact.
- **Manifests:** add a `.deps.json`, `obj/project.assets.json` or `packages.lock.json` for exact versions - they also carry the dependency graph, so every package gets its level (and indirect packages are covered). Opening a folder or archive picks the right one automatically.
- **Privacy:** only package names and versions are sent to `api.osv.dev` and, to find a package's dependencies, `api.nuget.org` - never your code or assemblies - and the UI asks for confirmation before the first scan. Set `Vulnerability:Enabled` to `false` to switch the feature off (for air-gapped use).
- **Try it:** [`samples/vulnerabilities`](samples/vulnerabilities) is a deliberately vulnerable test assembly (log4net 2.0.8, Newtonsoft.Json 12.0.1, System.Text.Json 8.0.0). Build it, open the DLL and press *Run scan* (no manifest needed: findings are *potential*); add its `.deps.json` and they become *confirmed*, with System.Text.Json *informational*. See [samples/README.md](samples/README.md) for every sample.

### More

- Open an assembly with the **Open** button or by dragging a `.dll` / `.exe` onto the page; several assemblies can be loaded at once. You can also open a **NuGet package (`.nupkg`)**, a **`.zip`** or a whole **folder** (the **Folder** button, or drop a folder): the best `lib/<framework>` assembly of a package, or the application assemblies of a publish folder, are opened (copies of the .NET framework are skipped, up to 8 at once) and the `.deps.json` that comes with them is used automatically for exact package versions in the Vulnerabilities Scan. They are listed in the explorer; click one to switch, and right-click it (or use the ✕ on hover) to **Reload** or **Remove** it.
- **Fit** and **PNG** export for graphs; the UI works offline (`vis-network` is bundled).
- Helpful errors for native or corrupt DLLs and a configurable upload limit; notifications are small toasts at the top right that disappear after a few seconds.
- Fully offline UI (except the optional vulnerability lookup).

---

## Quick start

### Docker (no .NET install required)

```bash
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:latest
```

Then open **http://localhost:8080** and upload a `.dll`. To use another host port, change the left side, e.g. `-p 5000:8080`.

To build the image yourself:

```bash
docker build -t dotnet-visualizer .
docker run --rm -p 8080:8080 dotnet-visualizer
```

The image has a `HEALTHCHECK` on `/healthz`; `docker ps` shows `healthy` once the app is up.

### Prebuilt downloads (no .NET install required)

Download the self-contained build for Windows (x64) or Linux (x64) from the [Releases page](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/releases), extract it and run the `dotnet-Assembly-Visualizer` executable. The app listens on port 8080.

### From source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
git clone https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer
cd dotnet-Assembly-Visualizer
dotnet run
```

Then open **http://localhost:8080**. (To run the `v2` line, `git checkout v2` first.)

### Windows service

`scripts/win/installer.ps1` publishes a self-contained build to `C:\DAV` and installs it as an auto-start Windows service (service name `DAV`, port 9999). Run it from an elevated PowerShell in the repository root.

---

## Using the UI

The views are arranged as a workflow: **what is in the assembly → how its parts relate → what is risky about it.** Start at the left of the tab bar and move right; a newly opened assembly starts at **Metrics**.

**Selecting things (ILSpy-style).** Pick a **class** in the explorer and the **Inheritance** view opens; expand the class to see its methods; pick a **method** and **Method Calls** opens. The class and method you chose stay visible in the **Selected** bar under the tabs in every view, each part is a link back to its view, and a method that is part of a circular call chain carries a red **⚠ circular dependency** link to that cycle (also shown as a note in Method Calls and a ⚠ in the explorer). Back / Forward (Alt+Left / Alt+Right) retrace your steps.

| Area | What it does |
|---|---|
| **Toolbar** | *Open* an assembly, package or zip, *Folder*, Back / Forward. |
| **Explorer** | Lists the loaded assemblies (right-click: Reload, Remove); browse namespaces, types and methods; search for types and methods; methods in a circular chain carry a red ⚠. |

### 1 · Assembly overview

| View | What it shows |
|---|---|
| **Metrics** | **About this assembly** (version, kind, platform, target framework, strong name, debug build), **obfuscation indicators** (evidence, never a verdict), then size and shape: method count, cyclomatic complexity (hotspots, histogram), IL size, most-called and most-dependent methods, largest types, and private/internal methods that nothing in the assembly references. *The unreferenced list is a lead, not proof* (reflection or another assembly can call them). |
| **Architecture** | How namespaces depend on each other (built from calls and inheritance), and where namespaces or types depend on each other in a circle - each with a concrete path, clickable. Field and parameter types are not part of the analysis. |

### 2 · Structure & relationships

| View | What it shows |
|---|---|
| **Inheritance** | Graph (or tree) of base types, interfaces and derived types of the selected class, or of the whole assembly; the depth choices come from the real hierarchy. |
| **Method Calls** | Callers and callees of the selected method; adjustable depth; each method appears once at its shortest distance; constructors are included. |
| **Circular Dependencies** | Every distinct circular call chain, grouped by class; click a method to jump to its call graph. Direct recursion is not reported as a cycle. |
| **C# Source** | The decompiled C# of the selected class or method (ICSharpCode.Decompiler, the engine behind ILSpy), with syntax highlighting; also reachable with the **C#** button in Method Calls. |

### 3 · Security & risk

| View | What it shows |
|---|---|
| **API Compatibility** | Choose two versions of an assembly: the public API changes, with the ones that can break callers marked (removed or changed members, narrower visibility, sealed types, added abstract members...), a suggested version bump, and Markdown / JSON export. |
| **Risky APIs** | Calls to APIs that deserve a security review (unsafe deserializers, Process.Start, Assembly.Load, weak crypto, DllImport, disabled TLS checks, and - because static analysis is blind there - reflection and dynamic code generation) with why each is risky, what to check, and links to the calling methods. **Heuristic leads - not vulnerabilities.** |
| **Vulnerabilities** | Known advisories for the packages the assembly depends on (levels 1-4, no manifest needed), checked against OSV.dev; summary by severity, filters, remediation, status tracking, PDF / JSON / SBOM / SARIF export. |

### How much to trust each result

| Kind | Where | Meaning |
|---|---|---|
| **Structure** | Metrics, Architecture, Inheritance, Method Calls, Circular Dependencies | Facts read from the assembly's metadata and IL, within the limits below. A cycle is a design smell, not a vulnerability. |
| **Heuristic** | Risky APIs, unreferenced methods | A pattern that deserves a look. The tool never calls these vulnerabilities. |
| **Confirmed** | Vulnerabilities | The exact package version is known and lies inside an advisory's affected range. |
| **Potential** | Vulnerabilities | The version was estimated (no manifest) or could not be verified against the advisory's ranges. |
| **Informational** | Vulnerabilities | A package the .NET runtime supplies: whether the vulnerable code runs depends on the runtime's patch level, which a scan cannot see. Shown for awareness; never counted by the command line's `--fail-on`. |

### Limitations

* **Obfuscation.** Structure and call-graph analysis is most accurate for unobfuscated assemblies. What was verified (see [`samples/obfuscation`](samples/obfuscation)): renamed members are listed under their obfuscated names, inheritance and calls survive renaming, and calls inside control-flow-flattened methods are still found (their order is lost and their complexity is very high). What cannot be resolved: packed or encrypted method bodies that are decrypted at run time, deliberately malformed metadata, and code generated at run time. When part of a file cannot be read the load says so, and the results are incomplete.
* **Calls that are not visible in the IL.** A call made through reflection (a method name in a string) is not an edge. A call through an interface or a delegate points at the interface / delegate, not at every implementation, so cycles that run through interface dispatch are not found.
* **Dependencies.** A scan without a manifest estimates package versions from assembly references and marks the findings *potential*; add a `.deps.json`, `project.assets.json` or `packages.lock.json` for exact versions.
* **Reference assemblies** (API only) contain no code; the load says so.

---

## Configuration

| Setting | Default | Notes |
|---|---|---|
| `Urls` | `http://*:8080` | Where the app listens. The Docker image pins it to `8080` through an environment variable. |
| `Upload:MaxFileSizeMB` | `200` | Maximum assembly size. Set it in `appsettings.json` or with the environment variable `Upload__MaxFileSizeMB`, e.g. `docker run -e Upload__MaxFileSizeMB=500 ...`. The UI shows the active limit next to *Open*. |
| `Vulnerability:Enabled` | `true` | Switch the vulnerability lookup off (e.g. air-gapped). Env var: `Vulnerability__Enabled`. |
| `Vulnerability:OsvBaseUrl` | `https://api.osv.dev/` | Vulnerability database endpoint (OSV API). |
| `Vulnerability:NuGetBaseUrl` | `https://api.nuget.org/v3-flatcontainer/` | NuGet flat container used to find packages and their dependencies. |
| `Vulnerability:TimeoutSeconds` | `20` | Timeout for each database request. |

Third-party: the **C# Source** tab uses [ICSharpCode.Decompiler](https://github.com/icsharpcode/ILSpy) (MIT licence) and the graphs use vis-network.

Endpoints: `POST /api/analysis/analyze` (analyse an uploaded assembly), `POST /api/analysis/diff` (public API changes between two versions), `POST /api/decompile` + `GET /api/decompile/{id}?token=` (C# source), `GET /api/analysis/limits`, `POST /api/vulnerabilities/scan`, `GET /api/vulnerabilities/status`, `GET /healthz`.

---

## How it works

- **Engine:** reads the PE metadata and scans IL bytes for `call` and `callvirt` opcodes to build the inheritance and call graphs; lambdas and compiler-generated methods are resolved back to the source methods that contain them.
- **Circular chains:** detected with a depth-first search over the call graph; the UI merges rotations of the same cycle so each chain is listed once.
- **Backend:** ASP.NET Core with a modular structure (`Controllers`, `Services`, `Interfaces`); `IAssemblyAnalyzer` and `ICircularDependencyDetector` are registered through dependency injection.
- **Frontend:** plain HTML, CSS and JavaScript with [vis-network](https://visjs.github.io/vis-network/) for graphs.

---

## Command line and CI

The same scan runs headless, for pipelines. It exits non-zero when a finding is at or above a severity, and it can write a SARIF file (GitHub code scanning) and a CycloneDX SBOM.

```bash
# from a build output (the .deps.json next to the assembly is used automatically)
dotnet dotnet-Assembly-Visualizer.dll scan bin/Release/net8.0/MyApp.dll --fail-on high --sarif scan.sarif --sbom sbom.cdx.json

# or with the container image
docker run --rm -v "$PWD:/work" ghcr.io/aizaz-hash/dotnet-visualizer-web:latest scan /work/MyApp.dll --fail-on high --sarif /work/scan.sarif
```

| Option | Meaning |
|---|---|
| `--manifest <file>` | `.deps.json`, `project.assets.json` or `packages.lock.json` for exact versions and levels (default: `<assembly>.deps.json` next to the assembly) |
| `--fail-on critical\|high\|medium\|low\|none` | exit code 1 when a finding is at or above it (default `high`) |
| `--allow-incomplete` | do not fail (exit 3) when not everything could be checked, e.g. no network |
| `--max-level 1-4` | how deep to follow package dependencies when there is no manifest |
| `--sarif`, `--sbom`, `--json <file>` | write SARIF 2.1.0, a CycloneDX 1.5 SBOM, or the full report |

Exit codes: `0` ok, `1` findings at/above `--fail-on`, `2` bad arguments or unreadable file, `3` scan incomplete (an unreachable database is never reported as a clean pass). Set `VULN_OSV_URL` / `VULN_NUGET_URL` to use a mirror.

A GitHub Action wraps it (`action.yml` in this repository):

```yaml
- uses: Aizaz-hash/dotnet-Assembly-Visualizer@v2
  with:
    assembly: src/MyApp/bin/Release/net8.0/MyApp.dll
    fail-on: high
- uses: github/codeql-action/upload-sarif@v3
  if: always()
  with:
    sarif_file: dependency-scan.sarif
```

In the web UI, **Export SBOM** and **Export SARIF** produce the same files from the scan on screen.

---

## Project structure

```
Controllers/     HTTP API (AnalysisController)
Services/        AssemblyAnalyzerService, CircularDependencyDetectorService
Interfaces/      IAssemblyAnalyzer, ICircularDependencyDetector
Services/Vulnerabilities/   OSV + NuGet clients, dependency resolver (levels 1-4), CVSS calculator, version ranges, scanner
util/            shared helpers
wwwroot/         index.html, css/, js/, lib/ (bundled vis-network)
tests/           automated tests: Unit/ (one component, fakes for the network), Integration/ (the real analyzer, scanner and API on the sample assemblies), Support/, and js/ (node --test)
tools/PerfPoc/   per-stage timing benchmark for the analyzer (see docs/performance/PERFORMANCE_REPORT.md)
samples/         sample assemblies to open and explore - one folder per scenario, documented in samples/README.md (they are inputs, not tests)
scripts/win/     Windows service installer
docs/images/     screenshots used in this README
.github/         CI and release workflows
```

---

## Testing

```bash
dotnet test tests/DotNetVisualizer.Web.Tests -c Release                                   # everything (builds the sample assemblies first)
dotnet test tests/DotNetVisualizer.Web.Tests -c Release --filter "FullyQualifiedName~Tests.Unit"          # unit tests only
dotnet test tests/DotNetVisualizer.Web.Tests -c Release --filter "FullyQualifiedName~Tests.Integration"   # integration tests only
node --test tests/js/*.test.js                                                             # browser-side logic
```

* **Unit tests** check one component at a time (CVSS and version ranges, the scanner and resolver with a fake OSV / NuGet, the exporters, the command line, the decompiler service, the API differ, the metrics and risky-API rules).
* **Integration tests** run the real analyzer, scanner, API differ and command line on the real [sample assemblies](samples/README.md): every "EXPECTED" statement in a sample's source header is asserted. Only OSV.dev and NuGet are replaced by fakes.
* **JavaScript tests** cover the depth model, the layout, the architecture analysis, the click and loader logic, and (from real analyzer output for three samples, `tests/js/fixtures`) the model behind the Method View -> Circular Dependency link. `scripts/update-js-fixtures` regenerates the fixtures and an integration test fails if they are stale.

The tests call no external service (OSV.dev and NuGet are faked), but building the sample assemblies restores their NuGet packages once, so the first build needs internet access. CI runs everything on every pull request.

---

## Releasing

Releases are built by the *Release* workflow and always use the **current tip of the release branch** (`main` for `0.x`, `v2` for `2.x`):

1. Merge everything for the release into the release branch.
2. GitHub → *Actions* → *Release* → *Run workflow*, choose the branch and enter the version (e.g. `2.0.1`). The workflow builds that branch's latest commit, publishes the GHCR image and the archives, and creates or moves the tag `v<version>` onto exactly that commit.

Pushing a tag by hand also works, but the workflow fails if the tag is not the tip of its release branch, so a stale tag cannot publish old code. Each release page states the branch and commit it was built from.

---

## License

This project is released under the **[MIT License](LICENSE)** - Copyright (c) 2026 Aizaz Zaheen.

The MIT License is a permissive, free-software license. Under its terms you may use, copy, modify, merge, publish, distribute, sublicense and sell copies of this software - for personal, educational and commercial purposes - free of charge, provided that the copyright notice and the permission notice are included in all copies or substantial portions of the software. The software is provided "as is", without warranty of any kind. See the [LICENSE](LICENSE) file for the full text.

---

## Contributing and contact

Bug reports, ideas and pull requests are welcome - open an issue or a PR on [GitHub](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer). I'd love to hear how you're using it.

- **GitHub:** [aizaz-hash](https://github.com/aizaz-hash)
- **Email:** [send2aizaz@gmail.com](mailto:send2aizaz@gmail.com?subject=Assembly%20Visualizer)

*Built by [Aizaz](https://github.com/aizaz-hash) - happy coding!*
