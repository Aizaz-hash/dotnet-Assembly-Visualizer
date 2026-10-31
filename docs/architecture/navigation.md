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

**Compare Versions** (the former *API Compatibility*) is a view of its own. The hint at the top of the Overview opens it with the current assembly already chosen as the old version, so it is discoverable from the page the user starts on without adding to the analysis navigation. It lives in its own group because it answers a different question than Security & risk (it needs two files, and a finding is a changed signature, not a vulnerability).

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

## Themes (Light, Dark, System)

Every colour of the interface is a CSS variable defined in `css/theme.css` once per theme; `css/styles.css` has no colour literals (a test enforces it, and that both themes define the same variables). The tokens are named by role and by the colour they have in the light theme: `--bg-f8f9fa` is a surface, `--fg-212529` text, `--bd-dee2e6` a border - so the light theme is exactly what the interface always looked like, and the dark theme sets the same names to colours made for a dark surface (neutrals rebuilt as a dark scale, pastel alert backgrounds as dark tints, dark status text as light text; buttons and badges keep their hue). Named groups cover the overlay, the C# viewer (`--code-*`) and the graphs (`--g-*`).

`js/theme.js` keeps the choice (`localStorage` key `av.theme`: system, light or dark; a missing or damaged value means system), resolves it to `<html data-theme="light|dark">`, listens for operating-system changes while the choice is System, and fires `themechange`. `app.js` redraws the open view then, because vis-network takes colours as values: `Theme.colors()` reads the `--g-*` variables. A tiny inline script in the page head sets the theme before the stylesheets load, so a dark choice never flashes white. Tests check the readability (WCAG AA, 4.5:1) of text, links, code and graph labels in both themes.

Adding a theme (for example high contrast) is one more variable block and one more option; no component CSS changes.

## The Overview tab and disabled tabs

Overview is one tab with three screens (Summary, Architecture, Metrics) chosen with a switch at the top of the page (`state.ovTab`). `ViewRegistry.resolve` maps the old names `architecture` and `metrics` to those screens, so every existing link keeps working. Without an assembly, only views marked `standalone` in the registry (Overview and Compare Versions) are usable; the other tabs are greyed out with the tooltip "Open an assembly first".
