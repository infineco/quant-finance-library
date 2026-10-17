using System;

namespace InFine.QuantFinance.Arithmetic.AutoDiff
{
    /// <summary>
    /// Forward-mode (tangent) dual number <c>v + t e</c> with <c>e^2 = 0</c>: a value and its derivative along one
    /// direction. Evaluating a function on duals whose tangent seeds a direction (1 on the input of interest, 0 elsewhere)
    /// returns the function value and its directional derivative, exact to rounding, in a single pass.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Kernel compatibility: a readonly struct of two doubles, operators and static functions only, no allocation,
    /// no exceptions, no interface dispatch and nothing but <c>Math.*</c>. Hybridizer can follow every call into it and a
    /// dual lives in two registers. One <see cref="Dual"/> pass gives one directional derivative; <see cref="Dual4"/>
    /// carries four directions in one pass.
    /// </para>
    /// <para>
    /// Branches follow the primal: comparisons (<c>&lt;</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>&gt;=</c>, <c>==</c>, <c>!=</c>)
    /// compare the values only and return a plain <see cref="bool"/>, so code written for doubles keeps its control flow and
    /// the derivative is that of the branch taken. At a non-smooth point the derivative is therefore one-sided:
    /// <see cref="Abs"/> takes the derivative of <c>+x</c> at 0, <see cref="Max(Dual, Dual)"/> and
    /// <see cref="Min(Dual, Dual)"/> return their first argument (value and tangent) on a tie, so <c>Max(x, 0)</c> at
    /// <c>x = 0</c> has derivative 1 (the at-the-money payoff <c>max(S - K, 0)</c> is differentiated as exercised) while
    /// <c>Max(0, x)</c> has derivative 0. Argument order is the only thing that selects the side.
    /// </para>
    /// <para>
    /// A direction along which an input does not move keeps an exact zero tangent through the elementary functions, even
    /// where their derivative is infinite (<see cref="Sqrt"/> or <see cref="Pow(Dual, double)"/> at 0): the chain rule is
    /// applied as <c>t == 0 ? 0 : t f'(v)</c> (see <see cref="Chain"/>), so constants never turn into NaN.
    /// </para>
    /// <para>
    /// Only first derivatives are carried. Second-order sensitivities (gamma, vanna, volga) need nested duals or, for Monte
    /// Carlo payoffs with kinks, likelihood-ratio weights; both are out of scope.
    /// </para>
    /// </remarks>
    public readonly struct Dual : IEquatable<Dual>
    {
        /// <summary>Primal value.</summary>
        public readonly double Value;

        /// <summary>Derivative of the value along the seeded direction.</summary>
        public readonly double Tangent;

        /// <summary>The dual <c>value + tangent e</c>.</summary>
        public Dual(double value, double tangent)
        {
            Value = value;
            Tangent = tangent;
        }

        /// <summary>A constant: zero tangent.</summary>
        public static Dual Constant(double value) { return new Dual(value, 0.0); }

        /// <summary>The independent variable: unit tangent.</summary>
        public static Dual Variable(double value) { return new Dual(value, 1.0); }

        /// <summary>A double is a constant dual.</summary>
        public static implicit operator Dual(double value) { return new Dual(value, 0.0); }

        /// <summary>
        /// Chain rule for a unary function f evaluated by the caller: returns <c>(f(v), f'(v) t)</c>, with an exact zero
        /// tangent when <c>t == 0</c> whatever <paramref name="derivative"/> is (infinite or NaN included).
        /// </summary>
        /// <param name="value">f(Value).</param>
        /// <param name="derivative">f'(Value).</param>
        public Dual Chain(double value, double derivative)
        {
            return new Dual(value, Tangent == 0.0 ? 0.0 : Tangent * derivative);
        }

        #region arithmetic

        /// <summary>Negation.</summary>
        public static Dual operator -(Dual a) { return new Dual(-a.Value, -a.Tangent); }

        /// <summary>Sum.</summary>
        public static Dual operator +(Dual a, Dual b) { return new Dual(a.Value + b.Value, a.Tangent + b.Tangent); }

        /// <summary>Sum with a constant.</summary>
        public static Dual operator +(Dual a, double b) { return new Dual(a.Value + b, a.Tangent); }

        /// <summary>Sum with a constant.</summary>
        public static Dual operator +(double a, Dual b) { return new Dual(a + b.Value, b.Tangent); }

        /// <summary>Difference.</summary>
        public static Dual operator -(Dual a, Dual b) { return new Dual(a.Value - b.Value, a.Tangent - b.Tangent); }

        /// <summary>Difference with a constant.</summary>
        public static Dual operator -(Dual a, double b) { return new Dual(a.Value - b, a.Tangent); }

        /// <summary>Constant minus a dual.</summary>
        public static Dual operator -(double a, Dual b) { return new Dual(a - b.Value, -b.Tangent); }

        /// <summary>Product: <c>(ab, a' b + a b')</c>.</summary>
        public static Dual operator *(Dual a, Dual b)
        {
            return new Dual(a.Value * b.Value, a.Tangent * b.Value + a.Value * b.Tangent);
        }

        /// <summary>Product with a constant.</summary>
        public static Dual operator *(Dual a, double b) { return new Dual(a.Value * b, a.Tangent * b); }

        /// <summary>Product with a constant.</summary>
        public static Dual operator *(double a, Dual b) { return new Dual(a * b.Value, a * b.Tangent); }

        /// <summary>Quotient: <c>(a/b, (a' - (a/b) b') / b)</c>.</summary>
        public static Dual operator /(Dual a, Dual b)
        {
            double q = a.Value / b.Value;
            return new Dual(q, (a.Tangent - q * b.Tangent) / b.Value);
        }

        /// <summary>Quotient by a constant.</summary>
        public static Dual operator /(Dual a, double b) { return new Dual(a.Value / b, a.Tangent / b); }

        /// <summary>Constant divided by a dual: <c>(a/b, -(a/b) b' / b)</c>.</summary>
        public static Dual operator /(double a, Dual b)
        {
            double q = a / b.Value;
            return new Dual(q, -q * b.Tangent / b.Value);
        }

        #endregion

        #region comparisons (on the value)

        /// <summary>Compares the values.</summary>
        public static bool operator <(Dual a, Dual b) { return a.Value < b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator >(Dual a, Dual b) { return a.Value > b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator <=(Dual a, Dual b) { return a.Value <= b.Value; }
        /// <summary>Compares the values.</summary>
        public static bool operator >=(Dual a, Dual b) { return a.Value >= b.Value; }
        /// <summary>Compares the values (tangents are ignored).</summary>
        public static bool operator ==(Dual a, Dual b) { return a.Value == b.Value; }
        /// <summary>Compares the values (tangents are ignored).</summary>
        public static bool operator !=(Dual a, Dual b) { return a.Value != b.Value; }

        /// <summary>Compares the value with a constant.</summary>
        public static bool operator <(Dual a, double b) { return a.Value < b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator >(Dual a, double b) { return a.Value > b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator <=(Dual a, double b) { return a.Value <= b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator >=(Dual a, double b) { return a.Value >= b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator ==(Dual a, double b) { return a.Value == b; }
        /// <summary>Compares the value with a constant.</summary>
        public static bool operator !=(Dual a, double b) { return a.Value != b; }

        /// <summary>Compares a constant with the value.</summary>
        public static bool operator <(double a, Dual b) { return a < b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator >(double a, Dual b) { return a > b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator <=(double a, Dual b) { return a <= b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator >=(double a, Dual b) { return a >= b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator ==(double a, Dual b) { return a == b.Value; }
        /// <summary>Compares a constant with the value.</summary>
        public static bool operator !=(double a, Dual b) { return a != b.Value; }

        /// <summary>Same value (consistent with <c>==</c>; tangents are ignored). Host only.</summary>
        public bool Equals(Dual other) { return Value == other.Value; }

        /// <summary>Same value as another <see cref="Dual"/> (tangents are ignored). Host only: boxes.</summary>
        public override bool Equals(object obj) { return obj is Dual other && Value == other.Value; }

        /// <summary>Hash of the value, consistent with <see cref="Equals(Dual)"/>.</summary>
        public override int GetHashCode() { return Value.GetHashCode(); }

        /// <summary>"value + tangent e". Host only.</summary>
        public override string ToString() { return Value.ToString("R") + " + " + Tangent.ToString("R") + "e"; }

        #endregion

        #region elementary functions

        /// <summary>e^x.</summary>
        public static Dual Exp(Dual x)
        {
            double e = Math.Exp(x.Value);
            return x.Chain(e, e);
        }

        /// <summary>Natural logarithm, derivative 1/x.</summary>
        public static Dual Log(Dual x)
        {
            return x.Chain(Math.Log(x.Value), 1.0 / x.Value);
        }

        /// <summary>Square root, derivative 1/(2 sqrt(x)) (infinite at 0, where a zero tangent stays 0).</summary>
        public static Dual Sqrt(Dual x)
        {
            double s = Math.Sqrt(x.Value);
            return x.Chain(s, 0.5 / s);
        }

        /// <summary>x^p for a constant exponent, derivative p x^(p-1).</summary>
        public static Dual Pow(Dual x, double p)
        {
            return x.Chain(Math.Pow(x.Value, p), p * Math.Pow(x.Value, p - 1.0));
        }

        /// <summary>a^y for a constant positive base, derivative ln(a) a^y.</summary>
        public static Dual Pow(double a, Dual y)
        {
            double v = Math.Pow(a, y.Value);
            return y.Chain(v, Math.Log(a) * v);
        }

        /// <summary>
        /// x^y for x &gt; 0: derivative <c>x^y (y' ln x + y x' / x)</c>. A direction along which neither moves gets 0.
        /// </summary>
        public static Dual Pow(Dual x, Dual y)
        {
            double v = Math.Pow(x.Value, y.Value);
            Dual fromX = x.Chain(v, y.Value * Math.Pow(x.Value, y.Value - 1.0));
            Dual fromY = y.Chain(v, Math.Log(x.Value) * v);
            return new Dual(v, fromX.Tangent + fromY.Tangent);
        }

        /// <summary>|x|; at 0 the derivative of +x is taken (tangent passed through unchanged).</summary>
        public static Dual Abs(Dual x)
        {
            return x.Value >= 0.0 ? x : -x;
        }

        /// <summary>Larger of the two; on a tie the first argument (value and tangent) is returned.</summary>
        public static Dual Max(Dual a, Dual b)
        {
            return a.Value >= b.Value ? a : b;
        }

        /// <summary>Smaller of the two; on a tie the first argument (value and tangent) is returned.</summary>
        public static Dual Min(Dual a, Dual b)
        {
            return a.Value <= b.Value ? a : b;
        }

        /// <summary>Sine.</summary>
        public static Dual Sin(Dual x)
        {
            return x.Chain(Math.Sin(x.Value), Math.Cos(x.Value));
        }

        /// <summary>Cosine.</summary>
        public static Dual Cos(Dual x)
        {
            return x.Chain(Math.Cos(x.Value), -Math.Sin(x.Value));
        }

        #endregion
    }
}
