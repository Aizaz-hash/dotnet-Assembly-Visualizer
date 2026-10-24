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
2. Open the **Vulnerabilities** tab, click **Add .deps.json...** and choose `VulnerableDemo.deps.json`.
3. Untick *This application's packages* if you only want the demo, then press **Run scan**.

Expected: 1 Critical and 3 High findings (plus lower-severity ones as advisories are published), all marked
*confirmed* because the exact package versions come from the `.deps.json`.

Independent cross-check with .NET's own audit:

```bash
dotnet list samples/VulnerableDemo package --vulnerable
```
