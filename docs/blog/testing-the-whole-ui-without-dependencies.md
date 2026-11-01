# Testing a whole web UI in a real browser, with no test dependencies

*2026-11-01 · by Aizaz*

.NET Assembly Visualizer has no bundler and no npm packages: the interface is plain HTML, CSS and JavaScript. I wanted one test that exercises all of it in a real browser, and I did not want to add a heavy test framework to a project that otherwise has none. It turned out Node 22 and the browser already on the machine are enough. Here is how the test works, and a few tricks that made it possible.

## The pieces

- **Node 22 has a WebSocket client built in**, so a script can talk to a browser's DevTools protocol without a library.
- **Edge or Chrome runs headless** (`--headless=new --remote-debugging-port=0`). The script reads the port from the `DevToolsActivePort` file the browser writes, creates a tab and connects.
- The test starts the app itself (`dotnet run`), points the browser at it, and then works like a user: it puts files into the file input, clicks rows in the explorer, tabs and links, and reads what the screen shows.

The driver is under 100 lines (`tests/ui/browser.mjs`). The checks that run inside the page are plain functions in one file (`page.js`). The test file (`ui.test.mjs`) is one `node:test` test with a sub-step per area.

## Tricks that made it practical

**Choosing a file like a user.** `DOM.setFileInputFiles` sets the files of an `<input type="file">` from disk paths and fires the real `change` event, so the app's normal open path runs.

**Standing in for a dialog you cannot open.** The Folder button uses the browser's directory picker, a native OS dialog no script can click. The test replaces only `showDirectoryPicker` with a function that returns a **real directory handle** from the browser's origin-private file system, filled with the files of a build folder, and records every `getFile()` call. Everything after the dialog is the real code, and the test can assert that the `.pdb` is never read.

**Seeing a flash.** A bug I wanted to prevent was a half-built page flashing at start-up. A script added with `Page.addScriptToEvaluateOnNewDocument` records the page state on every animation frame from the first moment of the load. The test then asserts the workspace was never visible before a view was active, and that the Theme box never showed the wrong value in a painted frame. Sampling per frame, not per DOM change, matters: DOM changes the user never sees would make the test fail for the wrong reason.

**Testing a live system change.** `Emulation.setEmulatedMedia` switches `prefers-color-scheme` while the app is open. The test checks that the System theme follows it in both directions, and that an explicit Light or Dark choice ignores it.

**Readability as an assertion.** For every visible piece of text on every screen, in both themes, the test computes the contrast against its real background and fails below 4.4:1.

## What it checks

15 checks, in about five minutes: start-up; every class and method of every sample assembly (the right view, the highlighted graph node, the right decompiled code); circular-dependency warnings and chains; navigation sequences with Back and Forward; search; bad files; the Folder button; theme switching and the live system theme; readability; layout from desktop to phone width; and a large real library (Newtonsoft.Json).

It found real problems while I was writing it. Property getters showed only `{ return 3; }` with no declaration, the cards on the Overview forced sideways scrolling in a narrow window, and an empty file was reported as "No DLL uploaded". It also found mistakes in the test itself, such as checking the code before it had finished loading, which is why the test waits for the decompiled text and not just for its label.

## Running it

```bash
node --test tests/ui/*.test.mjs
```

It needs the app and the sample assemblies built, and a Chromium browser; without them it skips and says why. In CI it runs on every pull request in headless Chrome.

The code is in [`tests/ui`](https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/tree/v3/tests/ui) if you want to borrow the pieces.
