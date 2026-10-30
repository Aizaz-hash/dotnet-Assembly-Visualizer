using System;
using System.Collections.Generic;
using System.Linq;

namespace Samples.MethodCalls
{
    // ---------------------------------------------------------------------------------------------------------------------
    // SAMPLE: method calls  -  open this assembly, expand "Pipeline" and select "Run" (Method Calls view).
    //
    // EXPECTED (edges only between methods of THIS assembly; framework calls such as Console.WriteLine are not edges):
    //   Pipeline.Run  -> Pipeline.Load -> Pipeline.Parse -> Pipeline.Validate           a 4-level chain: depth 1..4 all exist
    //   Pipeline.Run  -> Formatter.Format(int) / Format(string) / Format(int, int)      three overloads, each with its own node
    //   Helper.Log    is called from 3 methods                                          fan-in 3 (the edge is listed once per caller)
    //   Orchestrator.Everything  calls 6 different methods                              fan-out 6
    //   Service..ctor -> Service.Initialise                                             a constructor is a node when it calls something
    //   Mapper.Map<T> is a generic method                                               one node, edges from both callers
    //   Math.Factorial calls itself                                                     direct recursion is NOT a circular dependency
    // Result type: informational structure (nothing here is a finding).
    // ---------------------------------------------------------------------------------------------------------------------

    public class Pipeline
    {
        public void Run()
        {
            Load();
            var f = new Formatter();
            f.Format(1);
            f.Format("one");
            f.Format(1, 2);
            Helper.Log("run");
            Console.WriteLine("done");                    // framework call: not an edge
        }

        public void Load() { Parse(); Helper.Log("load"); }
        public void Parse() { Validate(); }
        public void Validate() { Helper.Log("validate"); }
    }

    public class Formatter
    {
        public string Format(int value) => value.ToString();
        public string Format(string value) => value.Trim();
        public string Format(int a, int b) => Format(a) + Format(b);
    }

    public static class Helper
    {
        public static void Log(string message) { Console.WriteLine(message); }
    }

    public class Orchestrator
    {
        public void Everything()
        {
            var p = new Pipeline();
            p.Run(); p.Load(); p.Parse(); p.Validate();
            new Formatter().Format(3);
            Helper.Log("everything");
        }
    }

    public class Service
    {
        public Service() { Initialise(); }
        private void Initialise() { Helper.Log("init"); }
    }

    public class Mapper
    {
        public TOut Map<TIn, TOut>(TIn input, Func<TIn, TOut> convert) => convert(input);
        public string FromInt(int i) => Map(i, x => x.ToString());
        public int FromString(string s) => Map(s, x => x.Length);
    }

    public static class Maths
    {
        public static int Factorial(int n) => n <= 1 ? 1 : n * Factorial(n - 1);          // direct recursion
    }

    public class Queries
    {
        public int CountBig(IEnumerable<int> values) => values.Where(v => v > 10).Count();   // lambda + LINQ (framework)
    }
}
