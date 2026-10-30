# Sample assemblies

Small, **deliberate** .NET class libraries that show what the visualizer can analyse. Open any of them with **Open** (or drag the `.dll` onto the page) and follow the "Open it in" column.
They are inputs for you to look at, **not** automated tests: the automated tests live in [`tests/`](../tests) and *use* these samples as realistic input
(see [How the tests use them](#how-the-tests-use-them)). None of this code is part of the application or its Docker image, and `vulnerabilities/` must never be deployed.

## Build them

```bash
bash scripts/build-samples.sh          # Linux / macOS / Git Bash
.\scripts\build-samples.ps1            # Windows PowerShell
```

Output: `samples/<scenario>/bin/Release/net8.0/<Name>.dll` (git-ignored). `dotnet test` builds them too.

## What each sample demonstrates

Result type: **Structure** = information about the code (never a finding) · **Heuristic** = a lead that needs a human decision (the tool says so) ·
**Confirmed / Potential / Informational** = vulnerability confidence (see the main README).

| Folder | Assembly | Open it in | What it contains | What the application should show | Result type |
|---|---|---|---|---|---|
| [`inheritance/`](inheritance) | `Inheritance.dll` | **Inheritance** | A 5-level class chain (`Shape → Polygon → Rectangle → Square → RoundedSquare`), a second branch, interfaces, a generic base type (`UserRepository : Repository<User>`), `IComparable<Square>`, three-level nested types | Depth choices 1 to 5 with none missing; interfaces as *implements*; the generic base type kept (not a placeholder) | Structure |
| [`method-calls/`](method-calls) | `MethodCalls.dll` | **Method Calls** | A 4-level call chain, three overloads, a method called from many places, one calling many, constructors, a generic method, lambdas, recursion, framework calls | Each overload its own node; framework calls (`Console.WriteLine`) are *not* edges; direct recursion is *not* an edge or a cycle; a constructor is a node when it calls something or is called | Structure |
| [`circular-dependencies/`](circular-dependencies) | `CircularDependencies.dll` | **Circular Dependencies**, then **Method Calls** (the warning and link) and **Architecture** | Seven method cycles (one class, three classes, mutual recursion, through constructors, entered from outside, two separate cycles), negative controls (recursion, a plain chain) and two namespaces that use each other | Seven distinct cycles; the entry method is not part of its cycle; a method in a cycle shows a warning with a link to the cycle; a `Billing` ↔ `Shipping` namespace cycle in **Architecture** | Structure (a design smell, not a vulnerability) |
| [`metrics/`](metrics) | `MetricsDemo.dll` | **Metrics** | One very branchy method, a trivial one, private/internal methods nothing calls, and look-alikes that *are* used (called, address taken, attribute, static initializer) | `Rules.Classify` is the top complexity hotspot; exactly the two never-called methods are listed as *unreferenced* | Structure; the unreferenced list is a **Heuristic** (reflection or another assembly could call them) |
| [`risky-apis/`](risky-apis) | `RiskyApis.dll` | **Risky APIs** | `BinaryFormatter`, `Process.Start`, `Assembly.LoadFrom`, MD5 / SHA-1, DES, an accept-everything TLS callback, a `DllImport`, and a class of safe code | One entry per use with severity, the reason it is risky and what to check; **no** entry for the safe class | **Heuristic** (an API is a lead, not an exploit) |
| [`vulnerabilities/`](vulnerabilities) | `VulnerableDemo.dll` + `.deps.json` | **Vulnerabilities Scan** | References `log4net 2.0.8`, `Newtonsoft.Json 12.0.1`, `System.Text.Json 8.0.0` (real, published advisories) | With the `.deps.json`: *Confirmed* findings for log4net and Newtonsoft.Json; *Informational* for `System.Text.Json` (the .NET runtime supplies it). Without it: versions are estimated, so findings are *Potential* | Confirmed / Potential / Informational |
| [`obfuscation/`](obfuscation) | `ObfuscationLike.dll` | **Inheritance**, **Method Calls**, **Metrics** | **Hand-written imitation** of obfuscation (not produced by a real obfuscator): renamed and look-alike names, a flattened state-machine method, a call made through reflection | Everything listed as named in the metadata; inheritance and calls survive renaming; calls inside the flattened method are still found (very high complexity); **no** edge for the reflective call (a documented limit) | Structure; the limit is documented, not hidden |
| [`api-compat/v1`](api-compat/v1) and [`v2`](api-compat/v2) | `ApiCompat.dll` (1.0.0.0 and 2.0.0.0) | **Compare** (choose v1 as *old*, v2 as *new*) | Two versions of one library with a known list of breaking, compatible and informational changes | Sixteen changes across seven types (12 breaking, 3 compatible, 1 note) and a *major* version bump suggested; v1 against itself is a *patch* with no changes | Breaking / Compatible / Note |

Each source file starts with a comment block listing its **EXPECTED** results; the integration tests check those statements, so the comments cannot silently go stale.

## Add a sample

1. Create `samples/<scenario>/<Name>.csproj` (an empty `<Project Sdk="Microsoft.NET.Sdk"></Project>` is enough - shared settings come from [`Directory.Build.props`](Directory.Build.props)) and a source file whose header comment says what it demonstrates and what is expected.
2. Add a row to the table above.
3. Add it to `tests/DotNetVisualizer.Web.Tests/DotNetVisualizer.Web.Tests.csproj` (a `ProjectReference` and a copy line) and to `SamplePaths`, and write an integration test for each EXPECTED line.

## How the tests use them

`tests/DotNetVisualizer.Web.Tests` builds the samples, copies the DLLs next to the test assembly and runs the **real** analyzer, scanner, differ and command line on them
(`Integration/Sample*Tests.cs`). Only the two network services (OSV.dev and NuGet) are replaced by fakes, so the tests never need internet access for the analysis itself.
The JavaScript tests read analysis JSON generated from three samples (`tests/js/fixtures`); `scripts/update-js-fixtures` regenerates it and an integration test fails if it is out of date.

## What the samples do NOT cover

* **Real obfuscators.** `obfuscation/` imitates three effects by hand. Encrypted/packed method bodies that are decrypted at run time, deliberately malformed metadata and code generated at run time are **not** covered and are not claimed to work.
* **Very large assemblies.** Performance is measured separately with `tools/PerfPoc` on the .NET runtime's own assemblies (see [`docs/performance`](../docs/performance)).
