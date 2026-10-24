# VulnerableDemo - a deliberately vulnerable test assembly

A small class library that references three NuGet packages with **known, published vulnerabilities**:

| Package | Version | Advisory |
|---|---|---|
| log4net | 2.0.8 | CVE-2018-1285 (Critical, CVSS 9.8) and others |
| Newtonsoft.Json | 12.0.1 | CVE-2024-21907 (High, CVSS 7.5) |
| System.Text.Json | 8.0.0 | CVE-2024-30105 and CVE-2024-43485 (High, CVSS 7.5) |

It exists only to try the **Vulnerabilities** view with real findings. It is **not** part of the application or its
Docker image and must never be deployed or referenced from real code.

## Build it

```bash
dotnet build samples/VulnerableDemo -c Release -o samples/VulnerableDemo/out
```

This produces `samples/VulnerableDemo/out/VulnerableDemo.dll` and `VulnerableDemo.deps.json`.

## Try it in the app

1. Start the visualizer and click **Open** to load `VulnerableDemo.dll`. Its types, inheritance and a
   circular call chain (`Normalizer.Normalize` -> `Validate` -> `Normalize`) appear as usual.
2. Open the **Vulnerabilities Scan** tab, click **Add .deps.json...** and choose `VulnerableDemo.deps.json`.
3. Press **Run scan** (the scan needs a loaded assembly; its result belongs to `VulnerableDemo.dll`).
4. Optionally press **Export PDF** (choose *Save as PDF*) or **Export JSON**.

Expected: 1 Critical, 5 High and 1 Medium finding (the exact list changes as advisories are published); the findings for log4net and Newtonsoft.Json are marked
*confirmed*, while the legacy System.* packages that the shared framework supplies are marked *potential*.

Independent cross-check with .NET's own audit:

```bash
dotnet list samples/VulnerableDemo package --vulnerable --include-transitive
```
