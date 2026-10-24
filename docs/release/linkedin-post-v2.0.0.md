🚀 dotnet Assembly Visualizer v2.0.0 is out — a ground-up rework of the whole tool.

I started this because I was tired of unreadable dependency graphs when trying to understand a .NET assembly. It reads the DLL's metadata and IL directly — no loading, no running. v2 turns it into a proper explorer, and adds a security check I really wanted.

🛡️ NEW: Vulnerabilities Scan
Checks the exact package versions of your assembly's dependencies against OSV.dev (GitHub advisories, NVD…) and puts Critical and High first:
• CVE, CVSS score, affected package, impact, fixed version and a copyable upgrade command
• Filters, status tracking (Open / Accepted Risk / Fixed), PDF and JSON export
• Findings are verified against the advisory's affected ranges: "confirmed" vs "potential" — not every dependency warning is treated as exploitable
• If the database can't be reached, it says "incomplete" — never a false "all clear"
• Read-only: it never touches your dependencies, and only package names and versions leave your machine

🔎 ILSpy-style explorer
Assembly → namespaces → types → methods, search, Back/Forward, several assemblies at once with Reload/Remove, drag-and-drop open.

🌳 Inheritance graph & tree — to a chosen depth, or the whole assembly
📞 Call graphs — each method appears once however many paths reach it; callers and callees to any depth
⚠️ Circular dependencies — clickable markers on affected methods, plus a view showing A() → B() → C() → A() with the loop-back marked

🏗️ Under the hood
Modular architecture with dependency injection, 86 automated tests, health checks, an offline-capable UI, MIT license, and a release workflow that always builds the current tip of the branch.

I tried the scan on a deliberately vulnerable sample (log4net 2.0.8, Newtonsoft.Json 12.0.1, System.Text.Json 8.0.0): 1 Critical (CVE-2018-1285, CVSS 9.8) + 5 High — the same advisories NuGet's own audit reports.

Try it:
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
→ open http://localhost:8080 and drop in a .dll

Release notes: https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/releases/tag/v2.0.0
Repo: https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer

Feedback and contributions are very welcome — especially on how the severity and confidence rules feel in real projects.

#dotnet #csharp #security #devsecops #opensource #devtools
