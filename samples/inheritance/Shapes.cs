using System;
using System.Collections.Generic;

namespace Samples.Inheritance
{
    // ---------------------------------------------------------------------------------------------------------------------
    // SAMPLE: inheritance  -  open this assembly and select "Shape" in the explorer (Inheritance view).
    //
    // EXPECTED:
    //   Shape -> Polygon -> Rectangle -> Square -> RoundedSquare     from RoundedSquare upwards: Square (1), Rectangle (2), Polygon (3),
    //                                                                Shape (4), IShape / INamed (5): depth choices 1 to 5, none missing
    //   Shape also -> Circle                                         a second branch
    //   Shape implements IShape and INamed                           interfaces are shown with an "implements" edge
    //   Square implements IComparable<Square>                        a generic interface instantiation
    //   UserRepository -> Repository<User>                           a generic base type
    //   Outer.Inner.Deepest                                          nested types (3 levels)
    // Result type: informational structure (nothing here is a finding).
    // ---------------------------------------------------------------------------------------------------------------------

    public interface IShape { double Area(); }

    public interface INamed { string Name { get; } }

    public abstract class Shape : IShape, INamed
    {
        public abstract double Area();
        public virtual string Name => "shape";
    }

    public class Polygon : Shape
    {
        public override double Area() => 0;
        public virtual int Sides => 3;
    }

    public class Rectangle : Polygon
    {
        public double Width { get; set; }
        public double Height { get; set; }
        public override double Area() => Width * Height;
        public override int Sides => 4;
    }

    public class Square : Rectangle, IComparable<Square>
    {
        public int CompareTo(Square other) => Area().CompareTo(other.Area());
    }

    public sealed class RoundedSquare : Square
    {
        public double Radius { get; set; }
    }

    public class Circle : Shape
    {
        public double Radius { get; set; }
        public override double Area() => Math.PI * Radius * Radius;
        public override string Name => "circle";
    }

    public class User { public string Id { get; set; } }

    public class Repository<T> where T : class
    {
        private readonly List<T> _items = new List<T>();
        public void Add(T item) => _items.Add(item);
        public int Count => _items.Count;
    }

    public class UserRepository : Repository<User>
    {
        public User Find(string id) => null;
    }

    public class Outer
    {
        public class Inner
        {
            public class Deepest
            {
                public int Value => 42;
            }
        }
    }
}
