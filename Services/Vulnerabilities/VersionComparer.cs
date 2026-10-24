using System.Text.Json;

namespace DotNetVisualizer.Web.Services.Vulnerabilities
{
    /// <summary>SemVer-style comparison (also fine for NuGet's 3-4 part versions) and OSV affected-range evaluation.</summary>
    public static class VersionComparer
    {
        public static int Compare(string a, string b)
        {
            var (an, apre) = Split(a);
            var (bn, bpre) = Split(b);

            int len = Math.Max(an.Count, bn.Count);
            for (int i = 0; i < len; i++)
            {
                long x = i < an.Count ? an[i] : 0;
                long y = i < bn.Count ? bn[i] : 0;
                if (x != y) return x < y ? -1 : 1;
            }

            // A release is newer than any of its pre-releases.
            if (apre.Length == 0 && bpre.Length == 0) return 0;
            if (apre.Length == 0) return 1;
            if (bpre.Length == 0) return -1;

            var ap = apre.Split('.');
            var bp = bpre.Split('.');
            for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
            {
                bool aNum = long.TryParse(ap[i], out var ax);
                bool bNum = long.TryParse(bp[i], out var bx);
                int c = aNum && bNum ? ax.CompareTo(bx)
                    : aNum ? -1
                    : bNum ? 1
                    : string.Compare(ap[i], bp[i], StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c < 0 ? -1 : 1;
            }
            return ap.Length.CompareTo(bp.Length);
        }

        private static (List<long> Numbers, string Pre) Split(string version)
        {
            var v = version.Trim().TrimStart('v', 'V');
            var plus = v.IndexOf('+');
            if (plus >= 0) v = v.Substring(0, plus);
            string pre = "";
            var dash = v.IndexOf('-');
            if (dash >= 0) { pre = v.Substring(dash + 1); v = v.Substring(0, dash); }

            var numbers = new List<long>();
            foreach (var part in v.Split('.', StringSplitOptions.RemoveEmptyEntries))
                numbers.Add(long.TryParse(part, out var n) ? n : 0);
            return (numbers, pre);
        }

        /// <summary>
        /// Evaluates one OSV "affected" entry for <paramref name="version"/>: true when the version is listed explicitly
        /// or falls inside an introduced/fixed (or last_affected) range.
        /// </summary>
        public static bool IsAffected(JsonElement affected, string version)
        {
            if (affected.TryGetProperty("versions", out var versions) && versions.ValueKind == JsonValueKind.Array)
                foreach (var v in versions.EnumerateArray())
                    if (v.ValueKind == JsonValueKind.String && Compare(v.GetString()!, version) == 0) return true;

            if (!affected.TryGetProperty("ranges", out var ranges) || ranges.ValueKind != JsonValueKind.Array) return false;

            foreach (var range in ranges.EnumerateArray())
            {
                var type = range.TryGetProperty("type", out var t) ? t.GetString() : null;
                if (type == "GIT") continue;                       // commit ranges say nothing about package versions
                if (!range.TryGetProperty("events", out var events) || events.ValueKind != JsonValueKind.Array) continue;

                string? introduced = null;
                foreach (var ev in events.EnumerateArray())
                {
                    if (ev.TryGetProperty("introduced", out var intro))
                    {
                        introduced = intro.GetString();
                    }
                    else if (introduced != null && ev.TryGetProperty("fixed", out var fix))
                    {
                        if (InRange(version, introduced, fix.GetString(), inclusiveEnd: false)) return true;
                        introduced = null;
                    }
                    else if (introduced != null && ev.TryGetProperty("last_affected", out var last))
                    {
                        if (InRange(version, introduced, last.GetString(), inclusiveEnd: true)) return true;
                        introduced = null;
                    }
                }
                if (introduced != null && InRange(version, introduced, null, inclusiveEnd: false)) return true;   // never fixed
            }
            return false;
        }

        private static bool InRange(string version, string introduced, string? end, bool inclusiveEnd)
        {
            if (introduced != "0" && Compare(version, introduced) < 0) return false;
            if (end == null) return true;
            int c = Compare(version, end);
            return inclusiveEnd ? c <= 0 : c < 0;
        }

        /// <summary>All "fixed" versions in the matching ranges of this affected entry, ordered ascending.</summary>
        public static List<string> FixedVersions(JsonElement affected)
        {
            var list = new List<string>();
            if (affected.TryGetProperty("ranges", out var ranges) && ranges.ValueKind == JsonValueKind.Array)
                foreach (var range in ranges.EnumerateArray())
                    if (range.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
                        foreach (var ev in events.EnumerateArray())
                            if (ev.TryGetProperty("fixed", out var fix) && fix.GetString() is { Length: > 0 } f) list.Add(f);
            list.Sort(Compare);
            return list;
        }
    }
}
