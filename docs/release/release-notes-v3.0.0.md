# dotnet Assembly Visualizer v3.0.0

A new interface. v3 turns the four views of v2 into one workflow: **Overview → Details → Security & risk → Compare**, with the decompiled C# next to everything, light and dark themes, and a command line for CI. Everything from v2.0.2 is included.

## A new way through an assembly

- **Overview.** The first screen after you open an assembly: name, version, full name, target framework, strong-name status with the **public key token**, module id, debug or release build, references. It shows **obfuscation indicators** (evidence, never a verdict, with a standing warning about what the analysis cannot do on obfuscated code) and "At a glance" cards that open the matching view. A switch at the top moves between **Summary**, **Architecture** (how namespaces depend on each other, and where they depend on each other in a circle) and **Metrics** (complexity hotspots, most-called and most-dependent methods, largest types, unreferenced methods).
- **Code Navigator.** One screen for what you pick. A **class** shows its inheritance (graph, tree, or the whole assembly) with its **C#** underneath; a **method** shows its callers and callees with that method's C# underneath. Property getters and setters show their property declaration. This replaces the separate Inheritance, Method Calls and C# Source tabs.
- **The selection is the context.** The class or method you pick never changes by itself. The **Selected** bar above every view links to the same selection in the other views, and a class that is part of a circular chain says so.
- **Circular Dependencies** follows the selection: the chains through the selected method, through the selected class, or all of them.

## New views

- **Risky APIs.** Calls that deserve a security review (unsafe deserializers, `Process.Start`, `Assembly.Load`, weak crypto, `DllImport`, disabled TLS checks, reflection and dynamic code) with why each is risky and where it is called. Heuristic leads, never presented as vulnerabilities.
- **Compare Versions.** Pick two versions of an assembly and see what changed in the public API and which changes can break callers, with a suggested version bump and Markdown / JSON export. It works without any assembly open.

## Themes

- **Light, Dark and System.** System follows the operating system or browser setting, live. The choice is remembered and applied before the page is painted, so there is no flash. The graphs and the C# viewer follow the theme, and all text is readable (WCAG AA) in both.

## Vulnerabilities

- Scanned **automatically in the background** when you open or select an assembly (after a one-time consent), with a spinner on the tab and a count when done; no repeated scans while a result is still valid.
- Notifications are small **toasts** on the right instead of a bar across the window.

## For CI

- A **command line** (`scan`), **SBOM** (CycloneDX) and **SARIF** export, and a **GitHub Action** (`action.yml`). In the web UI, *Export SBOM* and *Export SARIF* produce the same files.

## Opening files

- A DLL or EXE, a NuGet package, a zip, several files or a folder. In Chrome and Edge the **Folder** button uses the browser's directory picker (it asks to let the page *view* the folder), and only the assemblies and the `.deps.json` are read; the `.pdb` and the rest are left alone.
- Clearer errors: the native launcher `.exe` that `dotnet build` puts next to an application is explained and points to the `.dll`; an empty file is called empty.

## Fixes since v2.0.2

- A page no longer paints a half-built screen for a moment at start-up.
- The Overview cards, count rows and tab groups wrap on a narrow window instead of scrolling sideways.
- An upload that fails only blames the size limit when the file is over it; static files are re-checked on every load so a new script never runs next to an old one.
- Several smaller fixes to nested types, namespaces, debug / release detection and the framework shown for old assemblies.

## What changed for existing users

- The tabs are regrouped: **Metrics** and **Architecture** are inside **Overview**; **Inheritance**, **Method Calls** and **C# Source** are one **Code Navigator**; **API Compatibility** is now **Compare Versions** in its own group.
- The vulnerability scanner needs an open assembly (its tab is greyed out until one is open).
- Nothing in the configuration changed.

## Quality

- C# unit and integration tests (307), browser-side logic tests (140) and **one end-to-end UI test that drives a headless Chrome** through every area of the interface (15 checks), which runs in CI on every pull request.

## Upgrading

Docker: `docker pull ghcr.io/aizaz-hash/dotnet-visualizer-web:3.0.0` (the `latest` tag follows v3 from now on). Archives for Windows and Linux (x64) are attached to this release. The v2 line stays available on the `v2` branch.
