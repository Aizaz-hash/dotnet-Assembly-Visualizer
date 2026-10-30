using System;

namespace Samples.ApiCompat
{
    // VERSION 1 of the library. Compare it with ../v2 in the "Compare" view (see ../README.md for the expected list of changes).

    public class Customer
    {
        public string Name { get; set; }
        public int Age { get; set; }
        public void Save(int id) { }
        public string Describe() => "";
        public void Legacy() { }
        public static int Count() => 0;
        public const int Max = 10;
    }

    public interface IStore { void Put(string key); }

    public enum Level { Low = 1, High = 2 }

    public class Plugin { public virtual void Run() { } }

    public class Utility { public void Helper(int a) { } }

    public class Removed { }

    public class Unchanged { public int Value() => 1; }
}
