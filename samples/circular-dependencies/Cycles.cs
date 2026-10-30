using System;

namespace Samples.Cycles
{
    // ---------------------------------------------------------------------------------------------------------------------
    // SAMPLE: circular dependencies  -  open this assembly and look at the "Circular Dependencies" view; from a method that is
    // part of a cycle, the Method Calls view shows a warning with a link to that view.
    //
    // EXPECTED cycles (method level, found from the call graph):
    //   SimpleCycle        A -> B -> C -> A                          one class
    //   CrossClassCycle    Left.Step -> Middle.Step -> Right.Step -> Left.Step      three classes
    //   MutualRecursion    IsEven -> IsOdd -> IsEven
    //   ConstructorCycle   Ping..ctor -> Pong..ctor -> Ping..ctor    cycles through constructors are found too
    //   EntryOutside       Start -> (cycle X -> Y -> X)              Start is NOT part of the cycle, X and Y are
    //   TwoCycles          one cycle (P <-> Q) and a separate one (R -> S -> T -> R): 2 distinct cycles
    // EXPECTED non-cycles (negative controls - these must NOT be reported):
    //   Recursion.Factorial calls itself                              direct recursion is not reported as a circular dependency
    //   Acyclic.Top -> Middle -> Bottom                               a plain chain
    // Result type: informational structure; a cycle is a design smell, not a vulnerability.
    // ---------------------------------------------------------------------------------------------------------------------

    public class SimpleCycle
    {
        public void A() { B(); }
        public void B() { C(); }
        public void C() { A(); }
    }

    public class Left { public void Step(int n, Middle next) { if (n > 0) next.Step(n - 1, new Right(), this); } }
    public class Middle { public void Step(int n, Right next, Left back) { if (n > 0) next.Step(n - 1, back, this); } }
    public class Right { public void Step(int n, Left next, Middle back) { if (n > 0) next.Step(n - 1, back); } }

    public static class MutualRecursion
    {
        public static bool IsEven(int n) => n == 0 || IsOdd(n - 1);
        public static bool IsOdd(int n) => n != 0 && IsEven(n - 1);
    }

    public class Ping { public Ping() { _ = new Pong(); } }
    public class Pong { public Pong() { _ = new Ping(); } }

    public class EntryOutside
    {
        public void Start() { X(); }
        public void X() { Y(); }
        public void Y() { X(); }
    }

    public class TwoCycles
    {
        public void P() { Q(); }
        public void Q() { P(); }
        public void R() { S(); }
        public void S() { T(); }
        public void T() { R(); }
    }

    public static class Recursion
    {
        public static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);
    }

    public class Acyclic
    {
        public void Top() { Middle(); }
        public void Middle() { Bottom(); }
        public void Bottom() { Console.WriteLine("bottom"); }
    }
}

// EXPECTED namespace cycle (Architecture view): Billing and Shipping use each other, so neither can be used or released alone.
namespace Samples.Cycles.Billing
{
    public class Invoice
    {
        public Samples.Cycles.Shipping.Parcel Parcel { get; set; }
        public decimal Total() => Parcel == null ? 0 : Parcel.Cost();
    }
}

namespace Samples.Cycles.Shipping
{
    public class Parcel
    {
        public decimal Cost() => 5m;
        public decimal Invoiced(Samples.Cycles.Billing.Invoice invoice) => invoice.Total();
    }
}
