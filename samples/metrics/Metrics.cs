using System;

namespace Samples.Metrics
{
    // ---------------------------------------------------------------------------------------------------------------------
    // SAMPLE: metrics and unreferenced code  -  open this assembly and look at the "Metrics" view.
    //
    // EXPECTED (Metrics view, "Complexity hotspots"):
    //   Rules.Classify        highest complexity here (many branches, a switch and a catch)
    //   Rules.Trivial         complexity 1
    // EXPECTED ("Unreferenced methods" - private/internal methods nothing in the assembly calls; these are LEADS, not proof,
    // because reflection or another assembly could call them):
    //   Cleanup.NeverCalled          private, no caller            -> listed
    //   Cleanup.InternalNeverCalled  internal, no caller           -> listed
    // EXPECTED NOT LISTED (negative controls):
    //   Cleanup.UsedPrivate (called), Cleanup.ViaDelegate (its address is taken), Cleanup.Marked (has an attribute),
    //   Cleanup.PublicApi (public), Cleanup.OnlyFromStaticInit (used by the static constructor), constructors, overrides.
    // Result type: informational.
    // ---------------------------------------------------------------------------------------------------------------------

    public static class Rules
    {
        public static int Trivial() => 1;

        public static string Classify(int score, string kind)
        {
            if (score < 0) return "invalid";
            if (score == 0) return "zero";
            if (kind == null) return "unknown";
            switch (kind)
            {
                case "a": return score > 10 ? "A-high" : "A-low";
                case "b": return score > 10 ? "B-high" : "B-low";
                case "c": return score > 10 ? "C-high" : "C-low";
                case "d": return "D";
            }
            try { return Convert.ToString(score / (score - 5)); }
            catch (DivideByZeroException) { return "five"; }
        }
    }

    public class Cleanup
    {
        private static readonly int Seed = OnlyFromStaticInit();

        private int NeverCalled() => 1;
        internal int InternalNeverCalled() => 2;
        private int UsedPrivate() => 3;
        public int PublicApi() => UsedPrivate() + Seed;
        private void ViaDelegate() { }
        public Action Handler() => ViaDelegate;
        [Obsolete] private int Marked() => 4;
        private static int OnlyFromStaticInit() => 5;
    }
}
