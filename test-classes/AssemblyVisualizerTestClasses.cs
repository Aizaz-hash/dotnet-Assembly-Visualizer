namespace tests
{
public class BasicCallTests
{
    public void MethodA()
    {
        MethodB();
    }

    public void MethodB()
    {
        MethodC();
    }

    public void MethodC()
    {
    }

    public void NoCalls()
    {
        int value = 10;
        value++;
    }
}


// ============================================================
// 2. CROSS-CLASS CALLS
// ============================================================

public class CallerClass
{
    public void Start()
    {
        var service = new ServiceClass();

        service.Process();
    }

    public void CallHelper()
    {
        HelperClass.DoWork();
    }
}

public class ServiceClass
{
    public void Process()
    {
        HelperClass.DoWork();
    }

    public void ProcessAgain()
    {
        HelperClass.DoOtherWork();
    }
}

public static class HelperClass
{
    public static void DoWork()
    {
    }

    public static void DoOtherWork()
    {
    }
}


// ============================================================
// 3. SIMPLE CIRCULAR DEPENDENCY
//
// A -> B -> C -> A
// ============================================================

public class SimpleCycleA
{
    public void A()
    {
        B();
    }

    public void B()
    {
        C();
    }

    public void C()
    {
        A();
    }
}


// ============================================================
// 4. CROSS-CLASS CIRCULAR DEPENDENCY
//
// ClassA.A -> ClassB.B -> ClassC.C -> ClassA.A
// ============================================================

public class CycleClassA
{
    public void Start()
    {
        var b = new CycleClassB();

        b.Process();
    }

    public void Finish()
    {
        var b = new CycleClassB();

        b.Process();
    }
}

public class CycleClassB
{
    public void Process()
    {
        var c = new CycleClassC();

        c.Execute();
    }
}

public class CycleClassC
{
    public void Execute()
    {
        var a = new CycleClassA();

        a.Finish();
    }
}


// ============================================================
// 5. CYCLE WITH AN ENTRY METHOD OUTSIDE THE CYCLE
//
// Start
//   -> A
//      -> B
//         -> C
//            -> A
//
// The cycle itself starts at A, NOT Start.
// ============================================================

public class CycleWithEntryPoint
{
    public void Start()
    {
        MethodA();
    }

    public void MethodA()
    {
        MethodB();
    }

    public void MethodB()
    {
        MethodC();
    }

    public void MethodC()
    {
        MethodA();
    }
}


// ============================================================
// 6. TWO INDEPENDENT CYCLES
//
// Cycle 1:
// A -> B -> A
//
// Cycle 2:
// C -> D -> E -> C
// ============================================================

public class MultipleCycles
{
    public void A()
    {
        B();
    }

    public void B()
    {
        A();
    }

    public void C()
    {
        D();
    }

    public void D()
    {
        E();
    }

    public void E()
    {
        C();
    }

    public void SafeMethod()
    {
    }
}


// ============================================================
// 7. BRANCHING GRAPH
//
//          -> B ->
// A ->              D
//          -> C ->
//
// No cycle.
// ============================================================

public class BranchingGraph
{
    public void A()
    {
        B();
        C();
    }

    public void B()
    {
        D();
    }

    public void C()
    {
        D();
    }

    public void D()
    {
    }
}


// ============================================================
// 8. OVERLOADED METHODS
//
// Tests method signature generation and unique IDs.
// ============================================================

public class OverloadTests
{
    public void Process()
    {
    }

    public void Process(int value)
    {
    }

    public void Process(string value)
    {
    }

    public void Process(int value, string name)
    {
    }

    public void Caller()
    {
        Process();
        Process(10);
        Process("hello");
        Process(10, "hello");
    }
}


// ============================================================
// 9. OVERLOAD CALL CHAIN
//
// Tests whether the analyzer resolves the correct overload.
// ============================================================

public class OverloadChainTests
{
    public void Start()
    {
        Process(10);
    }

    public void Process(int value)
    {
        Finish(value);
    }

    public void Process(string value)
    {
    }

    public void Finish(int value)
    {
    }

    public void Finish(string value)
    {
    }
}


// ============================================================
// 10. GENERIC METHOD
//
// Exercises MethodSpecification handling.
// ============================================================

public class GenericMethodTests
{
    public void Start()
    {
        Process<int>(10);
        Process<string>("hello");
    }

    public void Process<T>(T value)
    {
        Helper();
    }

    public void Helper()
    {
    }
}


// ============================================================
// 11. GENERIC METHOD ACROSS CLASSES
// ============================================================

public class GenericCaller
{
    public void Start()
    {
        var service = new GenericService();

        service.Process<int>(100);
    }
}

public class GenericService
{
    public void Process<T>(T value)
    {
        Finish();
    }

    public void Finish()
    {
    }
}


// ============================================================
// 12. GENERIC TYPE
// ============================================================

public class GenericContainer<T>
{
    public void Start(T value)
    {
        Process(value);
    }

    public void Process(T value)
    {
    }
}

public class GenericContainerCaller
{
    public void Start()
    {
        var container = new GenericContainer<int>();

        container.Start(10);
    }
}


// ============================================================
// 13. INTERFACE IMPLEMENTATION
// ============================================================

public interface IProcessor
{
    void Process();
}

public class Processor : IProcessor
{
    public void Process()
    {
        Helper();
    }

    public void Helper()
    {
    }
}


// ============================================================
// 14. INTERFACE + IMPLEMENTATION + CALLER
// ============================================================

public interface IService
{
    void Execute();
}

public class Service : IService
{
    public void Execute()
    {
        Worker.DoWork();
    }
}

public static class Worker
{
    public static void DoWork()
    {
    }
}

public class ServiceCaller
{
    public void Start()
    {
        IService service = new Service();

        service.Execute();
    }
}


// ============================================================
// 15. INHERITANCE
// ============================================================

public class BaseClass
{
    public void BaseMethod()
    {
    }
}

public class DerivedClass : BaseClass
{
    public void DerivedMethod()
    {
        BaseMethod();
    }
}

public class MoreDerivedClass : DerivedClass
{
    public void MoreDerivedMethod()
    {
        DerivedMethod();
    }
}


// ============================================================
// 16. MULTI-LEVEL INHERITANCE
// ============================================================

public class LevelOne
{
}

public class LevelTwo : LevelOne
{
}

public class LevelThree : LevelTwo
{
}


// ============================================================
// 17. NESTED CLASSES
// ============================================================

public class OuterClass
{
    public void OuterMethod()
    {
        var inner = new InnerClass();

        inner.InnerMethod();
    }

    public class InnerClass
    {
        public void InnerMethod()
        {
            InnerHelper();
        }

        public void InnerHelper()
        {
        }
    }
}


// ============================================================
// 18. DEEPLY NESTED CLASSES
// ============================================================

public class Outer
{
    public class Middle
    {
        public class Inner
        {
            public void Execute()
            {
                Helper();
            }

            public void Helper()
            {
            }
        }
    }
}


// ============================================================
// 19. LAMBDA / DELEGATE
// ============================================================

public class LambdaTests
{
    public void Start()
    {
        Action action = () => LambdaTarget();

        action();
    }

    public void LambdaTarget()
    {
    }
}


// ============================================================
// 20. LAMBDA CAPTURING A LOCAL
// ============================================================

public class LambdaCaptureTests
{
    public void Start()
    {
        int value = 10;

        Action action = () =>
        {
            Console.WriteLine(value);
            Target();
        };

        action();
    }

    public void Target()
    {
    }
}


// ============================================================
// 21. MULTIPLE LAMBDAS
// ============================================================

public class MultipleLambdaTests
{
    public void Start()
    {
        Action first = () => FirstTarget();

        Action second = () => SecondTarget();

        first();
        second();
    }

    public void FirstTarget()
    {
    }

    public void SecondTarget()
    {
    }
}


// ============================================================
// 22. LINQ
//
// External calls should NOT become internal method edges.
// ============================================================

public class LinqTests
{
    public void Start()
    {
        var values = new[] { 1, 2, 3, 4, 5 };

        var result =
            values
                .Where(x => x > 2)
                .Select(x => x * 2)
                .ToList();

        Consume(result);
    }

    public void Consume(List<int> values)
    {
    }
}


// ============================================================
// 23. EXTERNAL FRAMEWORK CALLS
//
// Console.WriteLine / String methods / List methods should
// not appear as internal method graph targets.
// ============================================================

public class ExternalCalls
{
    public void Start()
    {
        Console.WriteLine("Hello");

        string value = "hello";

        value.ToUpper();

        var list = new List<int>();

        list.Add(10);

        Finish();
    }

    public void Finish()
    {
    }
}


// ============================================================
// 24. CONTROL FLOW
//
// Ensures the IL reader survives branches and constants.
// ============================================================

public class ControlFlowTests
{
    public void Start(int value)
    {
        if (value > 10)
        {
            First();
        }
        else
        {
            Second();
        }

        Third();
    }

    public void First()
    {
    }

    public void Second()
    {
    }

    public void Third()
    {
    }
}


// ============================================================
// 25. SWITCH
//
// Specifically exercises InlineSwitch handling.
// ============================================================

public class SwitchTests
{
    public void Start(int value)
    {
        switch (value)
        {
            case 0:
                First();
                break;

            case 1:
                Second();
                break;

            default:
                Third();
                break;
        }
    }

    public void First()
    {
    }

    public void Second()
    {
    }

    public void Third()
    {
    }
}


// ============================================================
// 26. MULTIPLE CALLS TO SAME METHOD
//
// Analyzer should deduplicate the edge.
// ============================================================

public class DuplicateEdgeTests
{
    public void Start()
    {
        Helper();
        Helper();
        Helper();
    }

    public void Helper()
    {
    }
}


// ============================================================
// 27. CALL CHAIN WITH EXTERNAL NOISE
//
// Internal:
//
// Start -> First -> Second -> Third
//
// External framework calls are mixed into the IL.
// ============================================================

public class InternalChainWithExternalCalls
{
    public void Start()
    {
        Console.WriteLine("Start");

        First();
    }

    public void First()
    {
        Console.WriteLine("First");

        Second();
    }

    public void Second()
    {
        string value = "hello";

        Console.WriteLine(value);

        Third();
    }

    public void Third()
    {
    }
}


// ============================================================
// 28. CIRCULAR DEPENDENCY THROUGH DIFFERENT CLASSES
//
// A.Start
//    -> B.Process
//       -> C.Execute
//          -> A.Finish
//             -> B.Process
//
// Actual cycle:
//
// B.Process -> C.Execute -> A.Finish -> B.Process
// ============================================================

public class AdvancedCycleA
{
    public void Start()
    {
        var b = new AdvancedCycleB();

        b.Process();
    }

    public void Finish()
    {
        var b = new AdvancedCycleB();

        b.Process();
    }
}

public class AdvancedCycleB
{
    public void Process()
    {
        var c = new AdvancedCycleC();

        c.Execute();
    }
}

public class AdvancedCycleC
{
    public void Execute()
    {
        var a = new AdvancedCycleA();

        a.Finish();
    }
}


// ============================================================
// 29. LONG CYCLE
//
// A -> B -> C -> D -> E -> F -> A
// ============================================================

public class LongCycle
{
    public void A()
    {
        B();
    }

    public void B()
    {
        C();
    }

    public void C()
    {
        D();
    }

    public void D()
    {
        E();
    }

    public void E()
    {
        F();
    }

    public void F()
    {
        A();
    }
}


// ============================================================
// 30. CYCLE + BRANCHES
//
// A -> B -> C -> A
//
// B -> Safe
// C -> External
// ============================================================

public class CycleWithBranches
{
    public void A()
    {
        B();
    }

    public void B()
    {
        C();
        Safe();
    }

    public void C()
    {
        A();

        Console.WriteLine("cycle");
    }

    public void Safe()
    {
    }
}

}
