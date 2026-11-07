# Phase 1 spike: loading a `netstandard2.0` module in PowerShell

Question: can one `netstandard2.0` module that uses `System.Reflection.Metadata` load in Windows PowerShell 5.1 and in PowerShell 7 (on .NET 8 and .NET 10)? Throw-away code; not part of the product.

Tested on Windows 11 with: Windows PowerShell 5.1 (.NET Framework 4.8), PowerShell 7.4.22 (.NET 8.0.33), PowerShell 7.6.9 (.NET 10.0.14). The cmdlet `Get-DavSpikeInfo <dll>` reads an assembly and prints its type count and the metadata library version it used.

| Variant | 5.1 | 7.4 (.NET 8) | 7.6 (.NET 10) |
|---|---|---|---|
| metadata library 10.0.9 | works | **fails**: `Could not load System.Reflection.Metadata 10.0.0.0` | works |
| metadata library 8.0.1 | **fails**: `The type initializer for 'PerTypeValues`1' threw` | works | works |
| metadata library 8.0.1 + assembly resolver (`ModuleInit.cs`) | works | works | works |

## What this means

1. **Pin the shared libraries to the oldest runtime we support (8.x).** A newer reference cannot be satisfied by an older host: PowerShell 7.4 already loads its own copy of `System.Reflection.Metadata` 8.0, and the module folder is not probed. Newer hosts (7.6) accept the older reference.
2. **Windows PowerShell 5.1 has no binding redirects.** The libraries that ship with the module (`System.Collections.Immutable`, `System.Memory`, `System.Buffers`, `System.Numerics.Vectors`, `System.Runtime.CompilerServices.Unsafe`) reference each other with different versions. `ModuleInit` (an `IModuleAssemblyInitializer`) answers `AppDomain.AssemblyResolve` from the module folder, which fixes it. It only runs for assemblies that could not be found, so it does nothing on PowerShell 7.
3. **Do not ship `System.Management.Automation.dll`** (it is only a reference stub from `PowerShellStandard.Library`).
4. **The module can be one `netstandard2.0` build** for all three hosts. A separate `net10.0` build is not needed for loading; it can come later if a PowerShell 7 only feature requires it.
5. Not yet tested: macOS and Linux (same PowerShell 7 code path as Windows, to be covered by CI in the last phase), and an isolated `AssemblyLoadContext` (not needed so far).

## Decision

Go. The plan's section 5 gets one rule: *the module is a single `netstandard2.0` build, pins `System.Reflection.Metadata` to 8.x, and carries the resolver from this spike.* This conflicts with the repository's current `System.Reflection.Metadata` 10.0.9; `Core` will use 8.0.1 (to be checked against the existing tests when Core is extracted).
