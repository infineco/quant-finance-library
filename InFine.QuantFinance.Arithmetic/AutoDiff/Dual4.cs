using System;

namespace InFine.QuantFinance.Arithmetic.AutoDiff
{
    /// <summary>
    /// Forward-mode dual number with four tangent directions: a value and its derivatives along four seeded directions,
    /// all propagated in one pass (for example delta, vega and rho of a Monte Carlo path at the cost of one path).
    /// Same algebra, branch and kink conventions as <see cref="Dual"/>; direction <c>i</c> of a <see cref="Dual4"/>
    /// computation equals a <see cref="Dual"/> computation seeded on that direction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Width and layout: four named scalar fields rather than a <c>fixed double[4]</c> buffer or an array. A fixed buffer
    /// needs <c>unsafe</c> code, cannot be a readonly field and is indexed through a pointer, which on a GPU sends the
    /// struct to local memory; named fields are plain scalars the compiler keeps in registers, every operation unrolls
    /// to four independent multiply-adds without a loop, and the struct stays blittable (40 bytes) for arrays of duals.
    /// Four covers the usual first-order Black-Scholes set (spot, volatility, rate, dividend or time) and keeps register
    /// pressure moderate; unused directions are simply left at zero. A wider sensitivity vector is better served by
    /// adjoint (reverse) mode than by a wider tangent.
    /// </para>
    /// <para>
    /// The chain rule of the elementary functions keeps an exact zero on any direction whose tangent is zero, as in
    /// <see cref="Dual"/> (see <see cref="Chain"/>).
    /// </para>
    /// </remarks>
    public readonly struct Dual4 : IEquatable<Dual4>
    {
        /// <summary>Number of tangent directions.</summary>
        public const int Width = 4;

        /// <summary>Primal value.</summary>
        public readonly double Value;

        /// <summary>Derivative along direction 0.</summary>
        public readonly double D0;

        /// <summary>Derivative along direction 1.</summary>
        public readonly double D1;

        /// <summary>Derivative along direction 2.</summary>
        public readonly double D2;

        /// <summary>Derivative along direction 3.</summary>
        public readonly double D3;

        /// <summary>The dual with the given value and tangents.</summary>
        public Dual4(double value, double d0, double d1, double d2, double d3)
        {
            Value = value;
            D0 = d0;
            D1 = d1;
            D2 = d2;
            D3 = d3;
        }

        /// <summary>A constant: all tangents zero.</summary>
        public static Dual4 Constant(double value) { return new Dual4(value, 0.0, 0.0, 0.0, 0.0); }

        /// <summary>The independent variable of direction <paramref name="direction"/> (0..3): unit tangent there, 0 elsewhere.</summary>
        public static Dual4 Variable(double value, int direction)
        {
            return new Dual4(value,
                direction == 0 ? 1.0 : 0.0,
                direction == 1 ? 1.0 : 0.0,
                direction == 2 ? 1.0 : 0.0,
                direction == 3 ? 1.0 : 0.0);
        }

        /// <summary>A double is a constant dual.</summary>
        public static implicit operator Dual4(double value) { return new Dual4(value, 0.0, 0.0, 0.0, 0.0); }

        /// <summary>Derivative along <paramref name="direction"/> (0..3); NaN for any other index.</summary>
        public double Tangent(int direction)
        {
            return direction == 0 ? D0 : direction == 1 ? D1 : direction == 2 ? D2 : direction == 3 ? D3 : double.NaN;
        }

        /// <summary>The single-direction dual of direction <paramref name="direction"/> (0..3).</summary>
        public Dual Direction(int direction) { return new Dual(Value, Tangent(direction)); }

        /// <summary>
        /// Chain rule for a unary function f evaluated by the caller: returns <c>(f(v), f'(v) t_i)</c>, with an exact
        /// zero on each direction whose tangent is 0, whatever <paramref name="derivative"/> is.
        /// </summary>
        /// <param name="value">f(Value).</param>
        /// <param name="derivative">f'(Value).</param>
        public Dual4 Chain(double value, double derivative)
        {
            return new Dual4(value,
                D0 == 0.0 ? 0.0 : D0 * derivative,
                D1 == 0.0 ? 0.0 : D1 * derivative,
                D2 == 0.0 ? 0.0 : D2 * derivative,
                D3 == 0.0 ? 0.0 : D3 * derivative);
        }

        #region arithmetic

        /// <summary>Negation.</summary>
        public static Dual4 operator -(Dual4 a) { return new Dual4(-a.Value, -a.D0, -a.D1, -a.D2, -a.D3); }

        /// <summary>Sum.</summary>
        public static Dual4 operator +(Dual4 a, Dual4 b)
        {
            return new Dual4(a.Value + b.Value, a.D0 + b.D0, a.D1 + b.D1, a.D2 + b.D2, a.D3 + b.D3);
        }

        /// <summary>Sum with a constant.</summary>
        public static Dual4 operator +(Dual4 a, double b) { return new Dual4(a.Value + b, a.D0, a.D1, a.D2, a.D3); }

        /// <summary>Sum with a constant.</summary>
        public static Dual4 operator +(double a, Dual4 b) { return new Dual4(a + b.Value, b.D0, b.D1, b.D2, b.D3); }

        /// <summary>Difference.</summary>
        public static Dual4 operator -(Dual4 a, Dual4 b)
        {
            return new Dual4(a.Value - b.Value, a.D0 - b.D0, a.D1 - b.D1, a.D2 - b.D2, a.D3 - b.D3);
        }

        /// <summary>Difference with a constant.</summary>
        public static Dual4 operator -(Dual4 a, double b) { return new Dual4(a.Value - b, a.D0, a.D1, a.D2, a.D3); }

        /// <summary>Constant minus a dual.</summary>
        public static Dual4 operator -(double a, Dual4 b) { return new Dual4(a - b.Value, -b.D0, -b.D1, -b.D2, -b.D3); }

        /// <summary>Product: <c>(ab, a' b + a b')</c> on each direction.</summary>
        public static Dual4 operator *(Dual4 a, Dual4 b)
        {
            return new Dual4(a.Value * b.Value,
                a.D0 * b.Value + a.Value * b.D0,
                a.D1 * b.Value + a.Value * b.D1,
                a.D2 * b.Value + a.Value * b.D2,
                a.D3 * b.Value + a.Value * b.D3);
        }

        /// <summary>Product with a constant.</summary>
        public static Dual4 operator *(Dual4 a, double b) { return new Dual4(a.Value * b, a.D0 * b, a.D1 * b, a.D2 * b, a.D3 * b); }

        /// <summary>Product with a constant.</summary>
        public static Dual4 operator *(double a, Dual4 b) { return new Dual4(a * b.Value, a * b.D0, a * b.D1, a * b.D2, a * b.D3); }

        /// <summary>Quotient: <c>(a/b, (a' - (a/b) b') / b)</c> on each direction.</summary>
        public static Dual4 operator /(Dual4 a, Dual4 b)
        {
            double q = a.Value / b.Value;
            return new Dual4(q,
                (a.D0 - q * b.D0) / b.Value,
                (a.D1 - q * b.D1) / b.Value,
                (a.D2 - q * b.D2) / b.Value,
                (a.D3 - q * b.D3) / b.Value);
        }

        /// <summary>Quotient by a constant.</summary>
        public static Dual4 operator /(Dual4 a, double b) { return new Dual4(a.Value / b, a.D0 / b, a.D1 / b, a.D2 / b, a.D3 / b); }

        /// <summary>Constant divided by a dual.</summary>
        public static Dual4 operator /(double a, Dual4 b)
        {
            double q = a / b.Value;
            double f = -q / b.Value;
            return new Dual4(q, f * b.D0, f * b.D1, f * b.D2, f * b.D3);
        }

        #endregion

        #region comparisons (on the value)

        /// <summary>Compares the values.</summary>
        public static bool operator <(Dual4 a, Dual4 b) { return a.Value < b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator >(Dual4 a, Dual4 b) { return a.Value > b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator <=(Dual4 a, Dual4 b) { return a.Value <= b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator >=(Dual4 a, Dual4 b) { return a.Value >= b.Value; }
        /// <summary>Compares the values (tangents are ignored).</summary>
        public static bool operator ==(Dual4 a, Dual4 b) { return a.Value == b.Value; }
        /// <summary>Compares the values (tangents are ignored).</summary>
        public static bool operator !=(Dual4 a, Dual4 b) { return a.Value != b.Value; }

        /// <summary>Compares the value with a constant.</summary>
        public static bool operator <(Dual4 a, double b) { return a.Value < b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator >(Dual4 a, double b) { return a.Value > b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator <=(Dual4 a, double b) { return a.Value <= b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator >=(Dual4 a, double b) { return a.Value >= b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator ==(Dual4 a, double b) { return a.Value == b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator !=(Dual4 a, double b) { return a.Value != b; }

        /// <summary>Compares a constant with the value.</summary>
        public static bool operator <(double a, Dual4 b) { return a < b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator >(double a, Dual4 b) { return a > b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator <=(double a, Dual4 b) { return a <= b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator >=(double a, Dual4 b) { return a >= b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator ==(double a, Dual4 b) { return a == b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator !=(double a, Dual4 b) { return a != b.Value; }

        /// <summary>Same value (consistent with <c>==</c>; tangents are ignored). Host only.</summary>
        public bool Equals(Dual4 other) { return Value == other.Value; }

        /// <summary>Same value as another <see cref="Dual4"/> (tangents are ignored). Host only: boxes.</summary>
        public override bool Equals(object obj) { return obj is Dual4 other && Value == other.Value; }

        /// <summary>Hash of the value, consistent with <see cref="Equals(Dual4)"/>.</summary>
        public override int GetHashCode() { return Value.GetHashCode(); }

        /// <summary>"value + [d0, d1, d2, d3] e". Host only.</summary>
        public override string ToString()
        {
            return Value.ToString("R") + " + [" + D0.ToString("R") + ", " + D1.ToString("R") + ", "
                + D2.ToString("R") + ", " + D3.ToString("R") + "]e";
        }

        #endregion

        #region elementary functions

        /// <summary>e^x.</summary>
        public static Dual4 Exp(Dual4 x)
        {
            double e = Math.Exp(x.Value);
            return x.Chain(e, e);
        }

        /// <summary>Natural logarithm, derivative 1/x.</summary>
        public static Dual4 Log(Dual4 x)
        {
            return x.Chain(Math.Log(x.Value), 1.0 / x.Value);
        }

        /// <summary>Square root, derivative 1/(2 sqrt(x)) (infinite at 0, where zero tangents stay 0).</summary>
        public static Dual4 Sqrt(Dual4 x)
        {
            double s = Math.Sqrt(x.Value);
            return x.Chain(s, 0.5 / s);
        }

        /// <summary>x^p for a constant exponent, derivative p x^(p-1).</summary>
        public static Dual4 Pow(Dual4 x, double p)
        {
            return x.Chain(Math.Pow(x.Value, p), p * Math.Pow(x.Value, p - 1.0));
        }

        /// <summary>a^y for a constant positive base, derivative ln(a) a^y.</summary>
        public static Dual4 Pow(double a, Dual4 y)
        {
            double v = Math.Pow(a, y.Value);
            return y.Chain(v, Math.Log(a) * v);
        }

        /// <summary>x^y for x &gt; 0, as <see cref="Dual.Pow(Dual, Dual)"/> on each direction.</summary>
        public static Dual4 Pow(Dual4 x, Dual4 y)
        {
            double v = Math.Pow(x.Value, y.Value);
            Dual4 fromX = x.Chain(v, y.Value * Math.Pow(x.Value, y.Value - 1.0));
            Dual4 fromY = y.Chain(v, Math.Log(x.Value) * v);
            return new Dual4(v, fromX.D0 + fromY.D0, fromX.D1 + fromY.D1, fromX.D2 + fromY.D2, fromX.D3 + fromY.D3);
        }

        /// <summary>|x|; at 0 the derivative of +x is taken (tangents passed through unchanged).</summary>
        public static Dual4 Abs(Dual4 x)
        {
            return x.Value >= 0.0 ? x : -x;
        }

        /// <summary>Larger of the two; on a tie the first argument (value and tangents) is returned.</summary>
        public static Dual4 Max(Dual4 a, Dual4 b)
        {
            return a.Value >= b.Value ? a : b;
        }

        /// <summary>Smaller of the two; on a tie the first argument (value and tangents) is returned.</summary>
        public static Dual4 Min(Dual4 a, Dual4 b)
        {
            return a.Value <= b.Value ? a : b;
        }

        /// <summary>Sine.</summary>
        public static Dual4 Sin(Dual4 x)
        {
            return x.Chain(Math.Sin(x.Value), Math.Cos(x.Value));
        }

        /// <summary>Cosine.</summary>
        public static Dual4 Cos(Dual4 x)
        {
            return x.Chain(Math.Cos(x.Value), -Math.Sin(x.Value));
        }

        #endregion
    }
}
