# v2.0.0 — a ground-up new release: ILSpy-style explorer, Vulnerabilities Scan, modular architecture

**dotnet Assembly Visualizer v2.0.0 is a complete rework of v0.x.** The UI is rebuilt as an ILSpy-style explorer, the backend is restructured into a modular architecture, and a new **Vulnerabilities Scan** checks your dependencies for known CVEs. It also includes every analyzer fix from v0.0.3.

![Vulnerability scan with a Critical finding selected](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-vuln-main.png)

## ⭐ Highlights

1. **🛡 Vulnerabilities Scan (new)** - exact package versions checked against OSV.dev, Critical and High first, with CVSS scores, impact, fixed versions, status tracking and PDF / JSON export.
2. **🔎 ILSpy-style explorer** - assembly → namespaces → types → base/derived types → methods, with search, Back / Forward and several assemblies loaded at once.
3. **🌳 Inheritance graph and tree** - base types, interfaces and derived types to a chosen depth, or the whole assembly.
4. **📞 Merged call graphs** - every method appears once however many paths reach it; callers and callees to a chosen depth.
5. **⚠ Circular dependency detection** - clickable markers on affected methods and a dedicated view with start and loop-back marked.
6. **🏗 Modular architecture + 86 automated tests** - controllers, services and interfaces with dependency injection, an offline-capable UI, health checks and a safer release process.

## 🆚 What changed since v0.0.3

| Area | v0.0.3 | v2.0.0 |
|---|---|---|
| UI | Single page with dropdowns and tabs | ILSpy-style explorer tree, tabbed views, Back / Forward, status bar, main-branch colours, larger readable scale |
| Assemblies | One at a time | Up to 8 loaded together; right-click **Reload** / **Remove**, drag-and-drop to open |
| Inheritance | Graph of the selected type's direct relations | Graph to depth 1 / 2 / 3 / 5 / all, **whole-assembly** graph, **tree** view, methods listed under each class |
| Method calls | Callers / callees of one method | Depth 1-4, callers / callees toggles, merged nodes and `×N` edges, "pick a method" guard for big classes, **Fit** and **PNG** export |
| Circular dependencies | List of chains | Duplicate-free chains, markers on methods, dedicated view, jump to each method, in-context note in the Method view |
| Vulnerabilities | - | **New**: OSV.dev scan with filters, details, remediation, status, PDF / JSON export |
| Architecture | Minimal API with static helpers | `Controllers/`, `Services/`, `Interfaces/` with dependency injection |
| Upload size | ASP.NET default (30 MB) | 200 MB, configurable, shown in the UI |
| Operations | - | `/healthz` endpoint, Docker `HEALTHCHECK`, container pinned to port 8080 |
| Offline use | `vis-network` loaded from a CDN | `vis-network` bundled (works offline, except the optional vulnerability lookup) |
| Tests | None | 86 automated tests, run in CI on every pull request |
| Releases | Tag-triggered | Release workflow builds the **current tip** of the release branch; stale tags are rejected |
| License | - | MIT |

## 🛡 Vulnerabilities Scan (new)

A new **Vulnerabilities Scan** tab checks the *exact* package versions of the loaded assembly's dependencies against [OSV.dev](https://osv.dev) (GitHub Security Advisories, NVD and others).

- **Critical and High first.** Severity is classified from the CVSS v3 vector (score computed per the FIRST.org specification), with the advisory's own rating as a fallback for CVSS v4-only advisories. Summary cards count findings per severity (Critical, High, Medium, Low, Unknown) and double as filters.
- **Everything you need per finding:** CVE / GHSA id, CVSS score and vector, affected component and version, weakness (CWE), potential impact and exploitation conditions derived from the vector, fixed versions, advisory text and references.
- **Actionable remediation:** the recommended upgrade version plus a ready-to-copy command (`dotnet add package …` / `npm install …`). The tool **never changes your dependencies** - the commands are for you to run.
- **Filters and status tracking:** filter by severity, component, status and confidence, or search by CVE / package. Mark findings **Open**, **Accepted Risk** or **Fixed**; a finding you marked as fixed re-opens automatically if the next scan still reports it. Export the report as **PDF** (print dialog → *Save as PDF*) or JSON.
- **Confirmed vs potential:** each finding is verified against the advisory's affected-version ranges. *Confirmed* means the exact version is in range and the package really ships code. *Potential* means that could not be established - for example a legacy `System.*` package the .NET shared framework supplies at runtime - so you are not told that every dependency warning is exploitable.
- **Honest about gaps:** an unreachable database, a failed component or a missing advisory record marks the scan **incomplete** - it never shows "no vulnerabilities" when something could not be checked.
- **What is scanned:** the dependencies of the loaded assembly with exact versions, from the `.deps.json` built with it (optionally also this tool's own packages). Results belong to the loaded assembly: they are kept while you switch between assemblies and disappear when you remove it. Assembly references of the loaded DLL are listed as an inventory (an assembly reference only carries an assembly version, which does not identify the NuGet package version).
- **Privacy:** only package names and versions are sent to `api.osv.dev` - never your code or assemblies - and the UI asks before the first scan. Turn the feature off with `Vulnerability:Enabled = false`.

![A potential finding explained](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-vuln-potential.png)

**Try it:** `samples/VulnerableDemo` is a deliberately vulnerable test assembly (log4net 2.0.8, Newtonsoft.Json 12.0.1, System.Text.Json 8.0.0). Build it, open the DLL, add its `.deps.json` in the Vulnerabilities Scan tab: it returns 1 Critical (CVE-2018-1285, CVSS 9.8), 5 High and 1 Medium finding, the same advisories NuGet's own audit reports (build warnings and `dotnet list package --vulnerable --include-transitive`). The application itself scans clean (38 components, 0 findings at release time).

## 🔎 Explorer and navigation

- **Assembly explorer:** assembly → namespaces → types → *Base Types*, *Derived Types* and methods; methods in a circular chain carry a red ⚠. Search finds types and methods.
- **Several assemblies at once** (up to 8): each is a root in the explorer; click to switch, right-click for **Reload** (re-analyses the same file) or **Remove**, or use the ✕ on hover. A removed assembly takes its scan results with it.
- **Open anywhere:** the *Open* button or drag a `.dll` / `.exe` onto the page. Native or corrupt files give a clear message instead of a generic error.
- **Back / Forward** (Alt+Left / Alt+Right) walk through everything you opened.
- **Clicking a class always opens its Inheritance view**; clicking a method opens the Method view, whose title is a breadcrumb back to the declaring type.

## 🌳 Inheritance

A graph of the selected type's base types, interfaces and derived types (bases above, derived below; interface implementations dashed). Choose the **depth** (1, 2, 3, 5 or all), switch to **Whole assembly** (capped at 300 types for readability), double-click a type to centre on it, or flip to the **Tree** for an expandable list in which every class expands to its methods.

![Inheritance graph](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-inheritance.png)

## 📞 Method calls

Callers and callees to a depth of 1-4 (toggle either side). Each method appears **once** however many paths reach it; repeated calls merge into one `×N` edge, and back-edges (recursion, cycles) arc over the flow in red. Types with many methods ask you to pick a method first instead of drawing an unreadable hairball. **Fit** and **PNG** export are built in.

![Method calls with circular dependency markers](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-calls.png)

## ⚠ Circular dependencies

Methods in a call cycle carry a clickable **⚠ Circular Dependency Detected** marker in the graph, and the Method view shows: *"This method may have a circular dependency. Click Circular Dependency View to investigate."* The dedicated view lists every distinct chain (`A() → B() → C() → A()`) - rotations of the same cycle are merged - with start and loop-back marked, and every method in a chain jumps to its call graph.

![Circular dependency view](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-cycles.png)

## 🏗 Under the hood

- **Architecture:** `Controllers/`, `Services/` and `Interfaces/` with dependency injection (`IAssemblyAnalyzer`, `ICircularDependencyDetector`, `IVulnerabilityScanner`); the frontend is plain HTML / CSS / JS with `vis-network` bundled.
- **Quality:** 86 xUnit tests cover the CVSS maths against reference scores, version-range matching, the scanner (confirmed / potential, incomplete scans, outages, duplicates, withdrawn advisories), the OSV client and the HTTP API. CI builds, tests and smoke-tests the Docker image on every pull request.
- **Operations:** `/healthz`, Docker `HEALTHCHECK`, container always listening on 8080, configurable upload limit.
- **Release process:** *Actions → Release → Run workflow* builds the current tip of the release branch and tags exactly that commit; pushing a stale tag is rejected. Each release page states the branch and commit it was built from.

### Configuration

| Setting | Default | Notes |
|---|---|---|
| `Urls` | `http://*:8080` | Where the app listens (the Docker image pins it to 8080). |
| `Upload:MaxFileSizeMB` | `200` | Maximum assembly size. |
| `Vulnerability:Enabled` | `true` | Turn the vulnerability lookup off (e.g. air-gapped). |
| `Vulnerability:OsvBaseUrl` | `https://api.osv.dev/` | Vulnerability database endpoint. |
| `Vulnerability:TimeoutSeconds` | `20` | Timeout per database request. |

### HTTP API

| Endpoint | Purpose |
|---|---|
| `POST /api/analysis/analyze` | Analyse an uploaded assembly (result now also lists `references`). |
| `GET /api/analysis/limits` | The active upload limit. |
| `POST /api/vulnerabilities/scan` | Scan a `.deps.json` and / or this tool's own packages. |
| `GET /api/vulnerabilities/status` | Whether scanning is enabled and which database is used. |
| `GET /healthz` | Health check. |

## ⬆️ Upgrading from v0.0.3

- **API route:** `POST /api/analyze` is now `POST /api/analysis/analyze`. Update scripts or integrations.
- **Project / DLL name:** `dotnet-Assembly-Visualizer`.
- **Upload limit:** the default is now 200 MB (was ASP.NET's 30 MB).
- **Network:** a vulnerability scan makes outbound HTTPS requests to `api.osv.dev`. Docker needs outbound access for that; disable the feature with `Vulnerability:Enabled = false`.
- **Docker:** same image name, new tags (`2.0.0`, `2.0`, `latest`); the container listens on 8080.

## 🐳 Docker

```bash
docker pull ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
```

Open http://localhost:8080. If port 8080 is taken, change the left side, e.g. `-p 8081:8080`.

## ✅ Verify your download

| File | SHA-256 |
|---|---|
| `dotnet-visualizer-2.0.0-win-x64.zip` | `0b8f8925ccf32d3404affaa0790dbdc7a71051e3c1a7257168afdc9b3befd397` |
| `dotnet-visualizer-2.0.0-linux-x64.tar.gz` | `4eec3967efb401e6e0462576528711fb7a9e8a6eca9742025dc626fe64c2e2d9` |

The Docker image carries its source commit in the label `org.opencontainers.image.revision`.

## Known limitations

- The .NET runtime / base image itself is not scanned - keep it patched through your SDK or base-image updates.
- CVSS v4 scores are not computed; for v4-only advisories the severity comes from the advisory's rating (and says so).
- Assembly references alone cannot be matched to package versions; add the `.deps.json` for an exact scan.
- A scan reflects the database at the moment you run it - new advisories appear over time, so scan again regularly.
- The whole-assembly inheritance graph shows at most 300 types; use the tree or a selected type for the rest.
- Graph generation is optimised for unobfuscated assemblies or basic obfuscation.
- Tested with the built-in browser and headless Edge; Chrome and Firefox are not yet covered.

**Full changelog:** https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/compare/v0.0.3...v2.0.0
