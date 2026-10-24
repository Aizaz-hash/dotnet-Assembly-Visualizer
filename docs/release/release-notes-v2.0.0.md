# v2.0.0 — Vulnerability testing, ILSpy-style explorer, modular architecture

**v2.0.0 adds built-in vulnerability testing.** Scan your dependencies for known CVEs, see **Critical and High** risks first, and get a verified, actionable fix for each - without leaving the assembly explorer. This release also brings an ILSpy-style explorer with inheritance graphs, merged call graphs and circular-dependency detection, on top of a modular architecture.

![Vulnerability scan with a Critical finding selected](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-vuln-main.png)

## 🛡 Vulnerability testing (new)

A new **Vulnerabilities** tab checks the *exact* package versions of your dependencies against [OSV.dev](https://osv.dev) (GitHub Security Advisories, NVD and others).

- **Critical and High first.** Severity is classified from the CVSS v3 vector (score computed per the FIRST.org specification), with the advisory's own rating as a fallback for CVSS v4-only advisories. Summary cards count findings per severity (Critical, High, Medium, Low, Unknown) and double as filters.
- **Everything you need per finding:** CVE / GHSA id, CVSS score and vector, affected component and version, weakness (CWE), potential impact and exploitation conditions derived from the vector, fixed versions, advisory text and references.
- **Actionable remediation:** the recommended upgrade version plus a ready-to-copy command (`dotnet add package …` / `npm install …`). The tool **never changes your dependencies** - the commands are for you to run.
- **Filters and status tracking:** filter by severity, component, status and confidence, or search by CVE / package. Mark findings **Open**, **Accepted Risk** or **Fixed**; a finding you marked as fixed re-opens automatically if the next scan still reports it. Export the report as JSON.
- **Confirmed vs potential:** each finding is verified against the advisory's affected-version ranges. *Confirmed* means the exact version is in range and the package really ships code. *Potential* means that could not be established - for example a legacy `System.*` package the .NET shared framework supplies at runtime - so you are not told that every dependency warning is exploitable.
- **Honest about gaps:** an unreachable database, a failed component or a missing advisory record marks the scan **incomplete** - it never shows "no vulnerabilities" when something could not be checked.
- **What is scanned:** the packages this application ships with, plus - when you add the `.deps.json` of the application you analysed - all of its dependencies with exact versions. Assembly references of the loaded DLL are listed as an inventory (an assembly reference only carries an assembly version, which does not identify the NuGet package version).
- **Privacy:** only package names and versions are sent to `api.osv.dev` - never your code or assemblies - and the UI asks before the first scan. Turn the feature off with `Vulnerability:Enabled = false`.

![A potential finding explained](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-vuln-potential.png)

**Try it:** `samples/VulnerableDemo` is a deliberately vulnerable test assembly (log4net 2.0.8, Newtonsoft.Json 12.0.1, System.Text.Json 8.0.0). Build it, open the DLL, add its `.deps.json` in the Vulnerabilities tab: it returns 1 Critical (CVE-2018-1285, CVSS 9.8) and 5 High findings, the same advisories NuGet's own audit reports (build warnings and `dotnet list package --vulnerable --include-transitive`). The application itself scans clean (38 components, 0 findings at release time).

## ✨ Explorer, graphs and circular dependencies

### ILSpy-style explorer
Assembly → namespaces → types → base/derived types and methods, search, Back / Forward navigation, Reload / Remove for every loaded assembly (right-click), drag-and-drop open.

### Inheritance graph and tree
A graph of the selected type's base types, interfaces and derived types (adjustable depth), or the whole assembly; click through to related types. A Tree toggle shows the same relationships as an expandable list.

![Inheritance graph](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-inheritance.png)

### Method calls
Callers and callees to a chosen depth; each method appears once however many paths reach it, repeated calls merge into one `×N` edge.

### Circular dependencies
Methods in a call cycle carry a clickable **⚠ Circular Dependency Detected** marker. The dedicated view lists every distinct chain (`A() → B() → C() → A()`) with start and loop-back marked, and jumps back to each method's call graph.

![Method calls with circular dependency markers](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-calls.png)

![Circular dependency view](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/raw/v2/docs/images/v2-cycles.png)

## 🏗 Under the hood
- Modular monolith (`Controllers/`, `Services/`, `Interfaces/`) with dependency injection; frontend split into HTML / CSS / JS; `vis-network` bundled so the UI works offline.
- **86 automated tests** (xUnit): CVSS reference scores, version-range matching, scanner behaviour with a fake database (confirmed/potential, incomplete scans, duplicates, withdrawn advisories), the OSV client and the HTTP API. CI runs them on every pull request.
- Configurable upload limit (`Upload:MaxFileSizeMB`, default 200 MB), `/healthz`, Docker `HEALTHCHECK`, container pinned to port 8080.
- Releases are built from the current tip of the release branch (Actions → Release → Run workflow).
- Includes the v0.0.3 analyzer fixes.

## ⚠️ Breaking changes
- The analyze endpoint moved from `POST /api/analyze` to `POST /api/analysis/analyze`; its result now also contains `references`.
- The project and DLL are named `dotnet-Assembly-Visualizer`.
- The default upload limit is 200 MB (ASP.NET's 30 MB default applied before).
- New endpoints `POST /api/vulnerabilities/scan` and `GET /api/vulnerabilities/status`; the app makes outbound HTTPS requests to `api.osv.dev` when you run a scan (disable with `Vulnerability:Enabled = false`).

## 🐳 Docker

```bash
docker pull ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
```

Open http://localhost:8080. The container needs outbound access to `api.osv.dev` for vulnerability scans.

## Known limitations
- The .NET runtime / base image itself is not scanned - keep it patched through your SDK or base-image updates.
- CVSS v4 scores are not computed; for v4-only advisories the severity comes from the advisory's rating (and says so).
- Assembly references alone cannot be matched to package versions; add the `.deps.json` for an exact scan.
- A scan reflects the database at the moment you run it - new advisories appear over time, so scan again regularly.
- Graph generation is optimised for unobfuscated assemblies or basic obfuscation.

**Full changelog:** https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/compare/v0.0.3...v2.0.0
