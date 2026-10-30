# VulnerableDemo - a deliberately vulnerable sample assembly

A small class library that references three NuGet packages with **known, published vulnerabilities**. It exists only to try the **Vulnerabilities Scan** with real findings. It is **not** part of the application or its Docker image and must never be deployed or referenced from real code.

| Package | Version | Advisory | What the scan should say |
|---|---|---|---|
| log4net | 2.0.8 | CVE-2018-1285 (Critical, CVSS 9.8) and others | **Confirmed** with the `.deps.json` (exact version inside the affected range); **Potential** without it (version estimated from the assembly reference) |
| Newtonsoft.Json | 12.0.1 | CVE-2024-21907 (High, CVSS 7.5) | Confirmed with the `.deps.json`; Potential without |
| System.Text.Json | 8.0.0 | CVE-2024-30105 and CVE-2024-43485 (High, CVSS 7.5) | **Informational**: the .NET runtime supplies this package, so whether the vulnerable code runs depends on the runtime's patch level, which a scan cannot see |

The exact list changes as advisories are published; the *classification* (confirmed / potential / informational) is what stays stable.

## Build it

```bash
dotnet build samples/vulnerabilities -c Release
```

This produces `samples/vulnerabilities/bin/Release/net8.0/VulnerableDemo.dll` and `VulnerableDemo.deps.json`.

## Try it in the app

1. Click **Open** and choose `VulnerableDemo.dll` (or open the whole `bin/Release/net8.0` folder with **Folder**: the `.deps.json` is then picked up automatically). Its types, inheritance and a circular call chain (`Normalizer.Normalize` → `Validate` → `Normalize`) appear as usual.
2. Open the **Vulnerabilities Scan** view and press **Run scan**. No manifest is required: the scan reads the assembly's references and follows the packages' dependencies to level 4 (findings are then *Potential* because the versions are estimated).
3. Optionally click **Add manifest…** and choose `VulnerableDemo.deps.json` (or `project.assets.json`): versions become exact and the findings *Confirmed*.
4. Optionally press **Export PDF**, **Export JSON**, **Export SBOM** or **Export SARIF**.

From the command line: `dotnet run --project . -- scan samples/vulnerabilities/bin/Release/net8.0/VulnerableDemo.dll --fail-on high` exits with code 1 (the Critical log4net finding); the *informational* System.Text.Json findings are shown but do not fail the run.

## Cross-check with NuGet's own audit

```bash
dotnet list samples/vulnerabilities package --vulnerable --include-transitive
```
