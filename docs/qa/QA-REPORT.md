# QA report - adversarial pass over the whole product

**Branch:** `v3` (views grouped into a workflow). **Method:** the product was exercised the way a user who wants to break it would, not only through its own tests.
Every statement below is something that was run or measured; where a claim could not be verified it is listed under *Not tested*.

## What was done

| Probe | How |
|---|---|
| 13 hostile or unusual uploads | empty file, one byte, text renamed `.dll`, a native DLL (`kernel32`), a truncated assembly (40 % and 90 %), corrupted metadata bytes, a corrupted PE header, 200 KB of random bytes, a reference assembly, an assembly whose references are missing, a 12 MB assembly (`System.Private.CoreLib`) - through `POST /api/analysis/analyze` |
| Metadata damage probe | corrupted 48 bytes at nine offsets inside the metadata block of a sample and recorded what happens |
| Old .NET Framework 4.x assemblies | `mscorlib`, `System.Core`, `System.Xml` through the same endpoint |
| Every view with every sample | in the built-in browser at 1280 px, with each sample open, reading the actual text of every empty, loading, error and normal state |
| Narrow window | the browser at phone width (375 px) |
| A very large assembly in the UI | `System.Private.CoreLib` (2,383 types, 30,000 methods, 25 MB of analysis) loaded in the browser; each view's first render timed |
| Navigation | class → Inheritance, method → Method Calls, the circular-dependency link from the Method Calls note and from the Selected bar, Back, Clear selection, switching assemblies |
| Claims against reality | each sentence of the README's UI, vulnerability and limitation sections was checked against what the product does |
| Automated | 297 C# tests (Unit + Integration on the real sample assemblies) and 71 JavaScript tests, all passing at the end |

## Summary

| Severity | Found | Fixed | Open | Accepted as is |
|---|---:|---:|---:|---:|
| Critical | 0 | 0 | 0 | 0 |
| High | 5 | 5 | 0 | 0 |
| Medium | 8 | 5 | 3 | 0 |
| Low | 7 | 7 | 0 | 0 |
| UX / polish | 7 | 1 | 4 | 2 |

No crash was found in the analyzer for any input: hostile inputs are rejected with a readable message (HTTP 400) or analysed. The High findings were all **wrong or silently incomplete results**, which is the worse kind of defect for an analysis tool.

## Findings

Format: **Issue · Severity · Location · Steps · Expected · Actual · Why it matters · Fix / recommendation.**

### High

**QA-01 · A class that derives from a generic type lost its base type** · High · Inheritance view, `AssemblyAnalyzerService` · *Fixed (PR #38)*
- Steps: open `samples/inheritance`, select `UserRepository` (`class UserRepository : Repository<User>`) in Inheritance.
- Expected: its base `Repository<T>`. Actual: no base type; also `Square : IComparable<Square>` showed no interface. The analyzer wrote a placeholder (`GenericTypeSpecification`) that the browser deliberately dropped.
- Why: a very common C# pattern (repositories, `IEquatable<T>`, `IEnumerable<T>`) silently disappeared from every hierarchy.
- Fix: a generic instantiation now resolves to the generic type. Regression: `A_class_that_derives_from_a_generic_instantiation_keeps_its_base_type` and a JS model test on real analyzer output.

**QA-02 · Nested types invented namespaces** · High · Explorer, Architecture, Metrics · *Fixed (PR #39)*
- Steps: open `samples/inheritance` and expand the namespace list.
- Expected: one namespace `Samples.Inheritance`. Actual: three - `Samples.Inheritance.Outer` and `Samples.Inheritance.Outer.Inner` appeared as namespaces, because the browser cut the namespace off the full name at the last dot.
- Why: wrong tree, wrong namespace counts, and false namespace "dependencies" in Architecture.
- Fix: the analysis carries each type's real namespace and a `nested` flag; nested types show as `Outer.Inner.Deepest`. Regression tests in C# and JS.

**QA-03 · Unreadable parts of a file were dropped without a word** · High · analyzer · *Fixed (PR #39)*
- Actual: every per-type, per-method and per-method-body failure was swallowed; a partly damaged, packed or protected file looked like a clean one with fewer methods.
- Expected: the user is told that the result is incomplete.
- Fix: skips are counted (`diagnostics`) and the load names them. A test proves healthy samples report *complete* diagnostics (so the warning cannot cry wolf). Honest limit: damage that only changes names cannot be detected (see QA-22).

**QA-04 · The page threw at start-up once Metrics became the first view** · High · `app.js` · *Fixed (PR #40) - introduced and caught during this task*
- Steps: load the page after making Metrics the default view.
- Actual: `ReferenceError: renderMetrics is not defined`; because the error happened at the end of `app.js`, the rest of the file (including the obfuscation-notice handler) never ran.
- Why: found only by opening the real page - no unit test loads the page. 
- Fix: the first render waits for `DOMContentLoaded`. New structure tests check script order, that every tab has a section and a renderer, the group order and the start view, so this class of mistake now fails a test.

**QA-05 · A package the .NET runtime supplies was reported as a vulnerability and failed builds** · High · Vulnerabilities, command line · *Fixed (PR #38)*
- Actual: `System.Text.Json 8.0.0` in a manifest (no runtime assets: the framework supplies it) gave a "potential" High finding that `--fail-on high` counted.
- Why: exposure depends on the runtime's patch level, which a scan cannot see - presenting it like a finding of the application's own packages is misleading and breaks pipelines for nothing.
- Fix: a third confidence level, **informational**: shown, filterable, exported as SARIF `note`, never counted by `--fail-on`. Confirmed / potential / informational are defined in the README and in the view.

### Medium

**QA-06 · Risky-API detection missed members of generic types** · Medium · Risky APIs · *Fixed* - `Expression<Func<int>>.Compile()` was invisible because the member's parent is a type specification. Regression: sample `CodeGeneration.Compile`.

**QA-07 · Old .NET Framework assemblies were treated as modern .NET** · Medium · Vulnerabilities · *Fixed* - an assembly without a `TargetFramework` attribute (everything built for .NET Framework 4.x, probed with `mscorlib`/`System.Core`) reported no target framework, so packages it needs from NuGet (`System.Memory`, `System.Text.Json`...) were skipped as "supplied by the runtime". The framework is now guessed from the references (`mscorlib` → .NET Framework) and the Metrics view says it is a guess.

**QA-08 · A reference assembly gave an empty, meaningless analysis** · Medium · load · *Fixed* - the SDK's `System.Runtime.dll` (API only: 10,521 methods, none of them real code) loaded as if it were code. It is now detected (including the case where the assembly defines the attribute itself) and the load says there is no real code to analyse.

**QA-09 · The README contradicted the product in three places** · Medium · docs · *Fixed* - it said references are "not matched to packages", described confirmed vs potential without informational, and pointed at a sample path that no longer existed; the obfuscation line was a blanket "not supported". All rewritten; the README now has a trust table and a limitations list.

**QA-10 · The obfuscation warning was vague and unconditional** · Medium · notice · *Fixed* - see "Obfuscation" below. The notice now says what was verified, and the Metrics view shows the evidence when a file looks obfuscated.

**QA-11 · Calls through an interface, a base type or a delegate are not edges** · Medium · Method Calls, Circular Dependencies · ***Open (documented, pinned by a test)***
- Steps: `samples/method-calls`, `Dispatcher.Go(IWorker)` calls `worker.Work()`.
- Actual: no edge from `Go` to anything; so a cycle that runs through interface dispatch is not found.
- Why: the largest source of false negatives in the call graph. It is stated in the README, in the sample and in a test that pins the behaviour.
- Recommendation: register interface/abstract methods as nodes and add "may call" edges to implementations (class-hierarchy analysis), drawn differently so they are not mistaken for certain calls.

**QA-12 · Calls made by name (reflection) are invisible** · Medium · ***Open (inherent)*** - by definition a static tool cannot see them. Mitigated: Risky APIs now lists reflection and dynamic code generation as leads so the reader knows where the graph is blind.

**QA-13 · Memory on large assemblies** · Medium · server · ***Open*** - analysing `System.Private.CoreLib` (12 MB) took 1.4 s and left the server at 523 MB working set (25 MB of JSON, the upload buffer and the object graph); the browser stayed at 104 MB JS heap and every view rendered within 100 ms. Not a problem for one user, but several concurrent large uploads would add up. Recommendation: build the call graph lazily per type and page the response.

### Low

| ID | Issue | Fixed |
|---|---|---|
| QA-14 | Native DLL error said "does not contain valid .NET assembly metadata" while other bad files said something else | Yes - "not a .NET assembly: it has no .NET metadata (a native / unmanaged library or another kind of file)" |
| QA-15 | Metrics showed "0 KB" for small assemblies | Yes - one decimal below 10 KB |
| QA-16 | Two classes both displayed as `a` looked like a bug (one uses a Cyrillic letter) | Yes - the explorer's tooltip names the non-ASCII characters |
| QA-17 | "1 circular chains" | Yes |
| QA-18 | "Include this tool's own packages" did not say it scans the *tool*, not your assembly | Yes - "Also scan this tool's own packages (not your assembly)" |
| QA-19 | Load summary said 29 methods, Metrics 28 | Yes - Metrics says "Methods with code" (abstract methods have none) |
| QA-27 | "About this assembly" said "debug" for a Release build (found while checking the new panel against the sample) | Yes - debug means optimisation disabled (`DebuggableAttribute`), not "has a pdb"; regression test follows the build configuration |

### UX / polish

| ID | Issue | Status |
|---|---|---|
| QA-20 | At phone width the layout overflows and the footer overlaps the content (the explorer takes the full width) | **Open** - it is a desktop tool; recommendation: a collapsible explorer under ~900 px |
| QA-21 | "Only namespaces in a cycle" stayed clickable when there are no cycles | **Fixed** - disabled with a tooltip that says why |
| QA-22 | Damage that only changes names/strings is accepted silently; structural damage is rejected (400). Probed at nine metadata offsets: eight rejected, one accepted unchanged | **Open, documented** - undetectable without a hash of the file |
| QA-23 | The generic obfuscation notice is always visible until dismissed, even for clearly clean code | **Open (deliberate for now)** - kept visible because the evidence-based indicator can miss obfuscators that keep readable names. Once the indicator has seen real protected binaries, show the notice only when it fires |
| QA-24 | Architecture ignores field and parameter types, so a dependency that exists only through them is not drawn | **Open, documented in the view and README** |
| QA-25 | The tab bar wraps to two rows below about 1000 px | **Accepted** - wrapping was chosen over hiding tabs off-screen |
| QA-26 | The Method Calls note links to "Circular Dependency View" while the tab is called "Circular Dependencies" | **Accepted** - cosmetic, the link works |

## Things that make no sense from a user's point of view (and what was done)

- A view named **Compare** inside a *security* group: renamed **API Compatibility**, because it answers "will my callers break?", which belongs with risk.
- **Two security views that look alike** (Risky APIs, Vulnerabilities): kept separate on purpose and labelled by *kind of result* - leads versus advisories - in the tab tooltips, the view subtitles and the README trust table.
- **A vulnerability finding that sounded certain** when the version was only estimated: it is *potential* and its note says the version was estimated; runtime-supplied packages are *informational*.
- **A graph or list without context**: the *Selected* bar now keeps the class and method visible in every view and links back.
- **"Nothing to check" shown as "Database unreachable"**: an assembly that only references the runtime has no packages to check; the view now says so (PR #27, described in the README).

## Obfuscation: what the evidence says

The old warning claimed that "deeply packed or control-flow flattened binaries are not supported". Checked against the implementation with `samples/obfuscation` (a hand-written imitation, labelled as such):

| Effect | Result | Test |
|---|---|---|
| Renamed / look-alike names | listed exactly as in the file; inheritance and calls survive renaming | `Renamed_symbols_are_listed...`, `Inheritance_and_calls_survive_renaming` |
| Control-flow flattening | the calls inside the flattened method **are found**; their order is lost and complexity is very high | `Calls_inside_flattened_control_flow...` |
| Calls through reflection | no edge (inherent) | `A_call_made_through_reflection_is_not_an_edge...` |
| Packed / encrypted bodies decrypted at run time, deliberately malformed metadata, run-time generated code | **not reproducible here, therefore not claimed** | - |

So the blanket statement was partly wrong (flattened control flow is *not* unsupported). The notice now states what was verified and what cannot be resolved, and the Metrics view reports indicators with their evidence: names of one or two letters, non-ASCII/control characters, obfuscator marker attributes. A clean result says it is not proof. The .NET runtime's own assemblies are not flagged (tested).

## Performance

| Measurement | Result |
|---|---|
| `System.Private.CoreLib` (12 MB) end to end in the browser | 1.4 s; JS heap 104 MB; Metrics / Architecture / Risky / Circular first render 23-95 ms |
| `System.Private.Xml` analysis | 0.24 s (before the earlier fix: 12.7 s) |
| Server memory after CoreLib | 523 MB working set (QA-13) |
| UI freeze | none observed; graphs are capped (120 nodes in Method Calls, 300 in Inheritance "whole assembly", 200 namespaces in Architecture) and the cap is stated on screen |

## Not tested

- Real obfuscator output (ConfuserEx, Dotfuscator, SmartAssembly...) - none was available; the sample is an imitation.
- Packed / encrypted binaries, assemblies with deliberately malformed metadata crafted by a tool (random damage was tried).
- Browsers other than the built-in Chromium; keyboard and screen-reader use; the Docker image and the GitHub Action on GitHub.
- Concurrent uploads and server behaviour under load.
- Assemblies larger than 50 MB, and the 200 MB upload limit.

## Assessment

**Ready for public users on a desktop, with the limitations stated.** After this pass the results shown are correct for the cases that were tested, anything the tool could not read is reported, heuristic results are labelled as leads, and the vulnerability confidence levels do not overstate. It is **not** ready to be called complete: calls through interfaces (QA-11) are the biggest accuracy gap, large-upload memory (QA-13) needs a lazy design before it is offered to many users at once, and the obfuscation indicators have only been proven against an imitation and the runtime's own assemblies.
