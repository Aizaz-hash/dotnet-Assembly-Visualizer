# dotnet Assembly Visualizer v2.0.2

A stability and accuracy release for the four views of v2: **Inheritance**, **Method Calls**, **Circular Dependencies** and the **Vulnerabilities Scan**. No new views. (Everything added after this point - Risky APIs, Metrics, Architecture, Compare, C# Source, the command line - lives on the `v3` branch.)

Includes everything since v2.0.0, including the changes first shipped as v2.0.1.

## Faster to open assemblies

- Opening a mid-sized assembly no longer feels stuck. The IL call analysis resolved every call target again at every call site; it now resolves each one once. **System.Private.Xml.dll: 12.7 s → 0.24 s** for the analysis (-98 %), and 0.9 s from picking the file to seeing the explorer. Measured with the new `tools/PerfPoc` benchmark; the report and raw numbers are in `docs/performance/`.
- A loading overlay shows while a file is uploaded (real percentage) and parsed (indeterminate bar and elapsed time). A corrupt or non-.NET file stops the loader, shows the reason, and leaves the app usable.

## Inheritance and Method Calls

- **Depth no longer skips values.** The Inheritance depth choices came from a fixed list (1, 2, 3, 5, all). Depth now comes from the real graph (1, 2, 3, 4, 5 ... up to what exists). Inheritance and Method Calls share one depth model: depth 0 is the start, a node's depth is its shortest distance, duplicates and cycles never change it.
- **Method Calls graphs are readable.** One large fixed spacing is replaced by a layered layout whose spacing follows node sizes, nodes per level and the edges between levels, so small graphs stay compact and dense ones do not pile up.
- **Constructor calls are in the graph.** `new Foo(...)` and `base(...)` are now edges, so cycles that pass through a constructor are found (they were missed before). Constructors are shown as `Foo(int a)`; the implicit default constructor is not listed.
- **Double-click expands a tree node** (types, namespaces, methods, folders) without selecting twice. Single-click is unchanged.

## Opening more than one DLL

- Open a NuGet package (`.nupkg`), a `.zip`, several files, or a whole folder (new **Folder** button, or drop a folder). A package opens its best `lib/<framework>` assembly; a publish folder opens the application assemblies and skips the copied .NET framework.
- The `.deps.json`, `project.assets.json` or `packages.lock.json` that comes with them is attached automatically for the vulnerability scan.

## Vulnerabilities Scan

- **No manifest needed.** The scan reads the assembly's own references (level 1), matches them to NuGet packages and follows each package's dependencies to **level 4** (level 0 is the assembly). Every dependency shows its level and a plain status: assembly discovered, package identified, version identified, vulnerability found, lookup unavailable, or could not be resolved. Estimated versions are marked *potential*.
- **Exact graphs from manifests.** `.deps.json`, `obj/project.assets.json` and `packages.lock.json` give exact versions and the full dependency graph, including indirect packages.
- **Honest results.** An assembly whose references are all supplied by the .NET runtime now says so ("No NuGet packages to check") instead of "Database unreachable". A database or NuGet outage is still never shown as a clean result.

## Fixes

- Scanning an assembly without a `.deps.json` reported nothing; it now resolves its dependencies on its own.
- The Inheritance depth list skipped 4.
- Method Calls graphs were extremely clustered.
- Loading had no visible progress and no error handling in the UI.
- A page in a background browser tab no longer stalls an upload.

## Upgrading

Docker: `docker pull ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.2`. Archives for Windows and Linux are attached to the release. No configuration changes are needed. The scan now also contacts `api.nuget.org` (package names and versions only) to find a package's dependencies; set `Vulnerability:Enabled` to `false` to switch both lookups off.
