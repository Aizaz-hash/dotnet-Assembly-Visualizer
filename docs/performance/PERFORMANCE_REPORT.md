# DLL parsing performance report

All numbers below were measured with `tools/PerfPoc` (Release build, .NET 8, `Stopwatch` high-resolution timers, one cold run
followed by the median of 3 warm runs) against assemblies from the installed .NET 8 runtime. Raw output:
[`baseline-before.txt`](baseline-before.txt) and [`after-optimized.txt`](after-optimized.txt).
Re-run it yourself:

```bash
dotnet run -c Release --project tools/PerfPoc -- --runs 5 path/to/Some.dll
```

## Problem

Loading a mid-sized assembly felt much slower than in ILSpy. Before changing anything, the analysis pipeline was instrumented
(`PipelineTimer`, returned to the UI as `timings` in the analysis result and written to the browser console as `[perf] load`) and
measured on a small, a medium and two large assemblies.

## Baseline (before any optimization)

| Assembly | Size | Types / methods | Total (analyze + JSON), median | Share spent in "call analysis (IL scan)" |
|---|---:|---|---:|---:|
| VulnerableDemo.dll | 5 KB | 6 / 6 | 0.20 ms | - |
| System.Linq.dll | 530 KB | 75 / 833 | 82.3 ms | 85 % (70.3 ms) |
| System.Private.CoreLib.dll | 12.9 MB | 2,383 / 30,455 | 957 ms | 73 % (694 ms) |
| **System.Private.Xml.dll** | 7.8 MB | 1,336 / 15,122 | **12,672 ms** | **99.2 % (12,577 ms)** |

Every other stage (load PE image, read metadata tables, lambda-name scan, type/method discovery, graph construction, cycle
detection, assembly references, JSON serialization) together took **under 300 ms even for the largest file**.

## Primary bottleneck

**Call analysis** - the second pass over every method body (`AssemblyAnalyzerService`, "call analysis (IL scan)").
For each `call`/`callvirt` instruction whose target is a `MemberReference` (any call to another assembly) the analyzer
re-decoded the target's signature, re-built its display strings and re-resolved its declaring type - at **every call site**,
although the same callee token is referenced from many places. Cost therefore scales with *call sites*, not with distinct
callees, which is consistent with what was measured: Xml (many calls into other assemblies) took 12.6 s while the larger CoreLib (mostly calls into itself, which take a cheaper path) took 0.7 s.

## Secondary observations

| Item | Measured | Verdict |
|---|---|---|
| Reflection (`GetProperty`/`GetValue`) over *all* method nodes inside `FindCanonicalMethodId` for each external call | replacing it with a per-class index changed Xml from 12,577 ms to 11,733 ms (within noise) | not the bottleneck, but cleaned up anyway (O(1) per class instead of reflection over every node) |
| `dynamic` loop to build the adjacency list | 3-11 ms (graph construction) | replaced with typed pairs |
| Type + method discovery | 45-166 ms | fine |
| Lambda-name scan (a second IL pass) | 1-20 ms | fine |
| JSON serialization of the result | 25-49 ms (15-21 MB payload) | fine on the server; payload size is the main cost for slow connections |
| Browser: JSON parse | 21 ms (Xml, 14.8 MB) | fine |
| Browser: build indexes + explorer + first view | 44 ms (Xml) | fine |
| Method View layout (new layered layout, 120 nodes / 238 edges) | 0.6 ms | negligible (the cost is vis-network drawing, not layout) |

## Root cause

Repeated, uncached resolution of the same call target at every call site (signature decoding and string building are allocation heavy).
Not the PE/metadata reader, not the graph or the layout, and not the UI.

## Solution implemented

Resolve each call-target metadata token once and reuse the answer (`resolvedCallTargets` memo keyed by token inside the IL pass).
The answer for a token never depends on the caller, so results are identical - all 86 existing tests and the new ones pass unchanged.
Supporting clean-ups: per-class method index instead of reflection, typed edge pairs instead of `dynamic`.

## Benchmark: before vs after (median of warm runs, ms)

| Stage | Linq before | Linq after | Xml before | Xml after | CoreLib before | CoreLib after |
|---|---:|---:|---:|---:|---:|---:|
| load PE image | 0.01 | 0.01 | 0.03 | 0.03 | 0.03 | 0.03 |
| read metadata tables | 0.06 | 0.05 | 0.51 | 0.52 | 1.16 | 0.85 |
| lambda name scan (IL) | 1.08 | 1.10 | 10.39 | 11.74 | 19.90 | 11.85 |
| type + method discovery | 4.99 | 5.42 | 45.49 | 54.51 | 165.67 | 149.02 |
| **call analysis (IL scan)** | **70.33** | **5.85** (-91.7 %) | **12,576.50** | **142.19** (-98.9 %) | **694.53** | **176.30** (-74.6 %) |
| graph construction | 0.14 | 0.07 | 10.95 | 3.36 | 11.65 | 4.33 |
| cycle detection | 0.22 | 0.22 | 8.24 | 7.06 | 12.18 | 9.83 |
| assembly references | 0.02 | 0.02 | 0.08 | 0.05 | 0.01 | 0.01 |
| JSON serialization | 5.74 | 4.93 | 29.03 | 24.94 | 48.64 | 43.49 |
| **TOTAL** | **82.31** | **19.72** (-76.0 %) | **12,671.72** | **239.89** (-98.1 %) | **957.41** | **390.08** (-59.2 %) |

Cold (first request after start-up, includes JIT): Xml 11,492 ms -> 589 ms. End to end in the browser (upload of the 7.8 MB file,
server analysis, 14.8 MB response, JSON parse, explorer + first view): **0.89 s** for System.Private.Xml.dll.

## Comparison with ILSpy

ILSpy was **not** run for this report, so no ILSpy numbers are claimed. The conceptual difference is real, though: ILSpy reads
metadata on demand and decompiles a member only when you select it, whereas this tool builds a whole-assembly call graph
(every method body's IL is scanned) before it shows anything. After this fix that eager pass costs 6-176 ms on the assemblies above,
so it is no longer what makes the tool feel slow; if a future assembly makes it so, the next step would be to build the call graph lazily
per type (not implemented - not justified by the measurements).

## Remaining limits

* The analysis response is still the whole graph (15-21 MB for the largest assemblies); a lazy/paged API would help on slow links.
* Method View is capped at 120 nodes per graph (unchanged) to keep drawing responsive.
* Numbers are from one machine; use `tools/PerfPoc` to compare on yours.
