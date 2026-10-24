# .NET Assembly Architecture Profiler — v2

I built this tool because I was tired of staring at massive, unreadable dependency graphs when trying to debug or understand how different parts of a complex .NET assembly talk to each other.

Most profilers require you to load the assembly into a running environment, which is slow, heavy, and overkill for a quick analysis. This tool takes a different approach—it reads the DLL binary directly, parses the metadata, and lets you visualize the architecture in seconds.

---

## 🚀 Quick Start

Run the latest published image — no .NET install required:

```bash
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:latest
```

Then open **http://localhost:8080** and upload your `.dll`.

---

## Screenshots

**Method Calls** - each method appears once; methods in a circular call chain get a clickable *Circular Dependency Detected* marker, and cyclic calls are drawn red and dashed.

![Method calls with circular dependency markers](docs/images/v2-calls.png)

**Circular Dependencies** - every distinct chain, with the start and the loop-back marked; click a method to jump to its call graph.

![Circular dependency view](docs/images/v2-cycles.png)

**Inheritance** - base types, interfaces and derived types as an expandable tree.

![Inheritance tree for a type](docs/images/v2-inheritance.png)

**Whole-assembly hierarchy** - every type hierarchy in the assembly, expanded lazily.

![Whole-assembly type hierarchy](docs/images/v2-all.png)

---

## What's new in v2

- **ILSpy-style explorer:** assembly tree (namespaces, types, base/derived types, methods), tabbed views, Back/Forward navigation, search, drag-and-drop open.
- **Inheritance tree** and **merged call graph:** each method appears once, repeated calls collapse into a single `xN` edge.
- **Circular dependency detection:** clickable markers on affected methods and a dedicated view for each distinct chain.
- **Modular Monolith architecture:** `Controllers/`, `Services/` and `Interfaces/`, with `IAssemblyAnalyzer` and `ICircularDependencyDetector` registered via DI.
- **Frontend split** into `wwwroot/css/styles.css` and `wwwroot/js/app.js`; `vis-network` is bundled so the UI works offline.

---

## Why I built this

* **Keep it lightweight:** It uses `System.Reflection.Metadata` to parse your DLL files directly. No runtime overhead, no slow loading times.
* **Cut through the noise:** If you've ever used a visualizer, you know they usually turn into a "spaghetti mess" of nodes. I added a surgical filter so you can pick *one* method and see exactly who calls it (and who it calls) without the extra clutter.
* **See the flow:** You can switch between a high-level **Inheritance View** (to see how your classes are structured) and a **Method View** (to trace actual execution flow).

---

## 🛠 What's under the hood

* **Backend:** ASP.NET Core with Controllers for clean API structure.
* **Engine:** Raw metadata and IL byte scanning (looking for those `call` and `callvirt` opcodes).
* **Frontend:** A clean, responsive interface powered by `vis-network` for that "physics-based" layout feel.

---

## 🐳 Running with Docker

The image pins the .NET version internally, so you don't need any specific .NET SDK/runtime installed on the host.

```bash
# Build the image
docker build -t dotnet-visualizer .

# Run it (the app listens on port 8080 inside the container)
docker run --rm -p 8080:8080 dotnet-visualizer
```

Then open **http://localhost:8080** and upload your `.dll`.

> Inside the container the port is fixed to 8080 by the `Urls` environment variable in the Dockerfile (it overrides `appsettings.json`). To use a different host port, change the left side, e.g. `-p 5000:8080`.

The image has a `HEALTHCHECK` that calls `/healthz`; `docker ps` shows `healthy` once the app is up.

### Upload limit

Files up to **200 MB** can be analysed. Change it with `Upload:MaxFileSizeMB` in `appsettings.json` or the environment variable `Upload__MaxFileSizeMB`, e.g. `docker run -e Upload__MaxFileSizeMB=500 ...`. The UI shows the active limit next to the upload button.

---

## Using the UI

The interface follows the ILSpy layout: an assembly explorer on the left, tabbed views on the right, a toolbar with Back / Forward (Alt+Left / Alt+Right) and a status bar.

- **Open:** click *Open* or drag a `.dll` / `.exe` anywhere onto the page. Several assemblies can be loaded in one session and switched from the toolbar.
- **Explorer:** assembly → namespaces → types → *Base Types*, *Derived Types* and methods. Methods that take part in a circular call chain are marked with a red ⚠. Use the search box to find types and methods quickly.
- **Inheritance:** a tree of the selected type's base types / interfaces and derived types. Click a type to navigate to it, ▸ to expand further. *Whole assembly* shows every type hierarchy.
- **Method Calls:** callers and callees of the selected method (depth 1-4). A method appears once however many paths reach it; repeated calls are shown as ×N on one edge. Double-click a node to re-centre on it.
- **Circular Dependencies:** methods that are part of a circular call chain get a clickable *⚠ Circular Dependency Detected* marker above them (and in the title bar). It opens the dedicated view listing every distinct chain through that method, e.g. `A() → B() → C() → A()`, with the start and the loop-back marked. Click a method in the chain to jump back to its call graph.
- **Fit / PNG** export the current graph. The app works offline: `vis-network` is bundled in `wwwroot/lib`.

---

## How to use it

1.  **Clone the repo:** `git clone https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer`
2.  **Switch to v2:** `git checkout v2`
3.  **Fire it up:** Just run `dotnet run`.
4.  **Explore:** Upload your `.dll`, pick a class, and start digging into the dependencies.

---

## Let's connect

I'm actively working on this, so if you find a bug or have an idea for a feature that would make your life easier, please reach out! I'd love to hear how you're using it.

* **GitHub:** [aizaz-hash](https://github.com/aizaz-hash)
* **Email:** [send2aizaz@gmail.com](mailto:send2aizaz@gmail.com?subject=Assembly%20Architecture%20Profiler%3F)

---
*Built by [Aizaz](https://github.com/aizaz-hash) — Happy coding!*

## License

Released under the [MIT License](LICENSE) - Copyright (c) 2026 Aizaz Zaheen.
