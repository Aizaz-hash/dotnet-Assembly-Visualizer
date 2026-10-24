I just shipped v2.0.0 of dotnet Assembly Visualizer — and the headline feature is the new Vulnerabilities Scan. 🛡️

Most tools show you a dependency warning. I wanted to know: is it a real, exploitable problem — and what exactly should I do about it?

The new Vulnerabilities Scan tab checks the exact package versions of your assembly's dependencies against OSV.dev (GitHub advisories, NVD and more) and puts Critical and High risks first:

🔴 CVE id, CVSS score (computed from the CVSS v3 vector), affected package and version
🔴 Impact and exploitation conditions, fixed versions, and a ready-to-copy upgrade command
🔴 Filters by severity, component, status and confidence; mark findings Open / Accepted Risk / Fixed; export a PDF report
🔴 Confirmed vs potential: every finding is verified against the advisory's affected ranges, and packages the .NET shared framework supplies at runtime are flagged as "potential" instead of being treated as exploitable
🔴 If the database can't be reached or something fails, the report says "incomplete" — it never pretends everything is clean

It never touches your dependencies, and only package names and versions leave your machine (never code or assemblies).

I tested it on a deliberately vulnerable sample assembly (log4net 2.0.8, Newtonsoft.Json 12.0.1, System.Text.Json 8.0.0): 1 Critical (CVE-2018-1285, CVSS 9.8) + 5 High — the same advisories NuGet's own audit reports. 86 automated tests cover the CVSS maths, version ranges and the "incomplete scan" paths.

v2 also brings an ILSpy-style explorer: inheritance graphs, merged call graphs and a circular-dependency view that jumps straight to the methods involved.

Try it:
docker run --rm -p 8080:8080 ghcr.io/aizaz-hash/dotnet-visualizer-web:2.0.0
→ open http://localhost:8080, upload a .dll, add its .deps.json in the Vulnerabilities Scan tab

Release notes: https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer/releases/tag/v2.0.0
Repo (MIT): https://github.com/Aizaz-hash/dotnet-Assembly-Visualizer

If you try it, I'd love feedback — especially on the severity/confidence rules. Contributions welcome.

#dotnet #csharp #security #devsecops #opensource #devtools
