namespace tests
{
    public class CircularTestA
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
            MethodA();
        }
    }

    public class CircularTestB
    {
        public void Start()
        {
            Process();
        }

        public void Process()
        {
            Execute();
        }

        public void Execute()
        {
            Finish();
        }

        public void Finish()
        {
            Process();
        }
    }

    public class CircularTestC
    {
        public void MethodA()
        {
            MethodB();
        }

        public void MethodB()
        {
            MethodA();
        }
    }

    public class CircularTestD
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
            MethodD();
        }

        public void MethodD()
        {
            MethodE();
        }

        public void MethodE()
        {
            MethodA();
        }
    }

    public class CircularTestE
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
            MethodB();
        }
    }

    public class CircularTestF
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
            // No circular dependency.
        }
    }

    public class CircularTestG
    {
        public void MethodA()
        {
            MethodA();
        }
    }

    public class CircularTestH
    {
        public void Start()
        {
            First();
        }

        public void First()
        {
            Second();
        }

        public void Second()
        {
            Third();
        }

        public void Third()
        {
            First();
        }

        public void Independent()
        {
            DoSomething();
        }

        public void DoSomething()
        {
            // No cycle.
        }
    }
}