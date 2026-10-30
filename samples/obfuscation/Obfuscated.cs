using System;
using System.Reflection;

// ---------------------------------------------------------------------------------------------------------------------
// SAMPLE: obfuscation  -  HAND-WRITTEN IMITATION of what an obfuscator does. It was NOT produced by a real obfuscator, so it
// shows the three effects the visualizer has to cope with, but it is not a substitute for testing against real protected binaries.
//
// 1. RENAMED SYMBOLS  - types/members are called "a", "b", look-alike letters, or share one name. EXPECTED: everything is listed
//    exactly as it is in the metadata (names are meaningless but structure, inheritance and calls are all still found).
// 2. CONTROL-FLOW FLATTENING - Flattened.Compute is a state machine ("while(true) switch(state)") instead of straight code.
//    EXPECTED: the calls inside it are still found (the analysis reads every call instruction), but the ORDER of the calls is
//    lost and its complexity is very high, which is how this shows up in the Metrics view.
// 3. HIDDEN CALLS - Indirect.Run calls Target.Secret through reflection by name. EXPECTED: NO edge from Indirect.Run to
//    Target.Secret. This is an inherent limit of static analysis (the target is a string at run time) and is documented, not fixed.
//
// Not reproducible here, and therefore NOT claimed to work: packed / encrypted method bodies that are decrypted at run time,
// metadata that is deliberately malformed, and code emitted at run time.
// ---------------------------------------------------------------------------------------------------------------------

namespace Samples.Obfuscation
{
    public class a
    {
        public int b(int c) => d(c) + 1;
        public int d(int c) => c * 2;
    }

    public class а : a                                // the first letter is a Cyrillic "a": it looks identical to the class above
    {
        public int b2(int c) => b(c);
    }

    public class Ꮳ { public void Ꮳ0() { new a().d(1); } }

    public static class Flattened
    {
        public static int Compute(int input)
        {
            int state = 0, result = 0;
            while (true)
            {
                switch (state)
                {
                    case 0: result = Step1(input); state = 3; break;
                    case 1: result = Step3(result); state = 4; break;
                    case 2: return result;
                    case 3: result = Step2(result); state = 1; break;
                    case 4: state = 2; break;
                    default: return -1;
                }
            }
        }

        private static int Step1(int x) => x + 1;
        private static int Step2(int x) => x * 3;
        private static int Step3(int x) => x - 2;
    }

    public class Target
    {
        public void Secret() { Console.WriteLine("secret"); }
    }

    public class Indirect
    {
        public void Run()
        {
            typeof(Target).GetMethod("Secret").Invoke(new Target(), null);          // resolved at run time: not an edge
        }
    }
}

namespace Samples.Obfuscation.Renamed
{
    // A block of identifiers the way a renaming obfuscator leaves them (one or two letters, reused everywhere).
    // EXPECTED: Metrics > "Obfuscation indicators" reports that this assembly looks obfuscated, with the share of one/two-letter names as evidence.
    public class aa { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class bb { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class cc { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class dd { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class ee { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class ff { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class gg { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class hh { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class jj { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
    public class kk { public int a; public int b; public int c() => a + b; public int d(int e) => e + c(); public int f() => d(1); }
}
