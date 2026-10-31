# Navigation, views and themes

How the screens are organised, where each one is declared, and how the planned **dark theme** and **view consolidation** fit in without a rewrite.

## The workflow

```
open a DLL → Overview → Details → Security & risk → Compare
```

| Group | Views | Question it answers |
|---|---|---|
| Overview | Overview (Summary, Architecture, Metrics) | What is this assembly, how do its namespaces depend on each other, and how big and complex is it? |
| Details | Code Navigator, Circular Dependencies | The class- and method-level detail behind the summaries. |
| Security & risk | Risky APIs, Vulnerabilities | What deserves a security review, and which dependencies have known advisories? |
| Compare | Compare Versions | What changed in the public API between two versions? |

**Compare Versions** (the former *API Compatibility*) is a view of its own. The Overview's **Compare with another DLL…** button opens it with the current assembly already chosen as the old version, so it is discoverable from the page the user starts on without adding to the analysis navigation. It lives in its own group because it answers a different question than Security & risk (it needs two files, and a finding is a changed signature, not a vulnerability).

## One list of views

`wwwroot/js/views.js` is the single declaration of groups and views (id, group, label, title, sub-title, whether the view works without an assembly). It is read by:

- `applyState` (which tab and section are active) and the tab click handler (which views open without an assembly),
- the Overview's cards (every card must lead to a registered view - tested),
- the tests, which compare the registry with the tab bar in `index.html` (same groups, same views, same order) and check that every view has a section, a renderer and a tab.

Adding a view = one entry in `views.js`, one tab button, one `<section id="view…">`, one `render…` function. Moving a view to another group = change `group` and move the button; the structure test fails until both agree.

## Data flow

All views render from the same analysis result (`rawAssemblyData`) and the same indexes built once in `buildIndexes()`; none of them fetches or computes the facts again. That is what makes merging views cheap: a view is a function from that shared state to DOM.

## Done: the Code Navigator

Inheritance, Method Calls and C# Source are one tab. The selection decides the screen (`navMode()` in `app.js`): a selected method shows the call graph, otherwise the selected class shows its inheritance, and each has the C# code of the selection in a panel below (one shared `renderSource`, one switch to hide it). The registry entry lists the two page sections it owns (`sections`), so the tab bar, the activation logic and the tests still read one list. Circular Dependencies stays separate: it has its own list-and-chain layout.

## Planned: further view consolidation

Idea: one vertically scrollable *analysis screen* with the C# code, the method view and the dependencies as stacked panels, instead of one tab each. Nothing has to be rewritten for it:

1. Each detail view already has an independent root (`viewCalls`, `viewSource`, `viewCycles`) and a `render…` function that fills it from the selection (`state.type`, `state.method`).
2. A *composite* view is a new registry entry whose section contains the existing roots as panels; its renderer calls the existing renderers in order. The tabs that it replaces are removed from the registry (and the tab bar) in the same change.
3. The selection bar (`#contextBar`) is already shared by all views, so the panels stay in sync.
4. Navigation calls such as `navigate({ view: 'calls', … })` keep working if the old ids are kept as *aliases* that open the composite view scrolled to the right panel.

Things to decide when it is built: whether the graph canvases (vis-network) can live in a scrolling page (they need an explicit height), and whether panels render lazily when scrolled into view (the C# decompile is the expensive one).

## Planned: dark theme

State today: the UI colours are plain values in `styles.css`; the notification toasts are the first part that uses **tokens** (`--toast-*` custom properties, one set per type). The plan:

1. Introduce tokens on `:root` for the roles the UI uses: surface (`--surface`, `--surface-raised`), text (`--text`, `--text-muted`), border, accent, and the severity/status colours already used by the cards and badges (`--sev-critical … --sev-low`, `--ok`, `--warn`, `--bad`).
2. Replace literal colours in `styles.css` by those tokens, one file section at a time (the Overview, toasts and tab strip already use a small, regular set of colours, so they are a good start). The graph canvases take their colours from `graph-utils.js` and `architecture-utils.js`; give those a `theme()` function that reads the tokens with `getComputedStyle`.
3. Add `[data-theme="dark"]` (and `@media (prefers-color-scheme: dark)` as the default) that only redefines the tokens. No component CSS changes after step 2.
4. Store the choice in `localStorage` with a try/catch (it must work without storage), toggle in the toolbar.

Because components never name a colour after step 2, a later theme (high contrast, for example) is another token block.

## The Overview tab and disabled tabs

Overview is one tab with three screens (Summary, Architecture, Metrics) chosen with a switch at the top of the page (`state.ovTab`). `ViewRegistry.resolve` maps the old names `architecture` and `metrics` to those screens, so every existing link keeps working. Without an assembly, only views marked `standalone` in the registry (Overview, Vulnerabilities, Compare Versions) are usable; the other tabs are greyed out with the tooltip "Open an assembly first".
