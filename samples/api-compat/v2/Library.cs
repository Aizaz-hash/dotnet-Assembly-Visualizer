using System;

namespace Samples.ApiCompat
{
    // VERSION 2 of the library: deliberately contains breaking, compatible and informational changes relative to ../v1.

    public sealed class Customer                                   // BREAKING: now sealed
    {
        public string Name { get; }                                // BREAKING: the setter was removed
        // Age was removed                                         // BREAKING: removed member
        public void Save(long id) { }                              // BREAKING: Save(int) became Save(long) (one signature change)
        public int Describe() => 0;                                // BREAKING: return type string -> int
        [Obsolete] public void Legacy() { }                        // NOTE: now [Obsolete]
        public int Count() => 0;                                   // BREAKING: static -> instance
        public const int Max = 20;                                 // BREAKING: a constant's value changed
        public void Rename(string name) { }                        // COMPATIBLE: new member
    }

    public interface IStore { void Put(string key); void Delete(string key); }     // BREAKING: abstract member added (implementers must add it)

    public enum Level { Low = 1, High = 3, Critical = 4 }          // BREAKING: High changed 2 -> 3;  COMPATIBLE: Critical added

    public class Plugin { public void Run() { } }                  // BREAKING: no longer virtual (overrides stop compiling)

    public class Utility { protected void Helper(int a) { } }      // BREAKING: public -> protected (narrower)

    // Removed was deleted                                         // BREAKING: type removed

    public class Fresh { }                                         // COMPATIBLE: new type

    public class Unchanged { public int Value() => 1; }
}
