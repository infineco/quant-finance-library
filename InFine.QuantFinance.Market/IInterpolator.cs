using System;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.Market
{
    /// <summary>
    /// A one-dimensional interpolation scheme on nodes <c>(x_i, y_i)</c>. Implementations are stateless and
    /// thread-safe; the numerics live in <see cref="InterpolationKernels"/>.
    /// </summary>
    public interface IInterpolator
    {
        /// <summary>A short name, for diagnostics.</summary>
        string Name { get; }

        /// <summary>
        /// True when the interpolant on <c>[x_i, x_{i+1}]</c> depends only on the two nodes <c>i</c> and <c>i + 1</c>.
        /// A sequential bootstrap is exact in a single pass only for local schemes; non-local ones need global iteration.
        /// </summary>
        bool IsLocal { get; }

        /// <summary>Builds the interpolant through the nodes, copying them.</summary>
        /// <exception cref="ArgumentException">
        /// No node, lengths differ, or the abscissas are not finite and strictly increasing, or an ordinate is not finite.
        /// </exception>
        IInterpolation Interpolate(ReadOnlySpan<double> x, ReadOnlySpan<double> y);
    }

    /// <summary>
    /// An interpolant built by an <see cref="IInterpolator"/>: immutable, thread-safe and allocation-free to evaluate.
    /// Outside the node range it continues the polynomial of the end segment.
    /// </summary>
    public interface IInterpolation
    {
        /// <summary>The interpolated value.</summary>
        double Value(double x);

        /// <summary>The derivative of the interpolant (the right derivative at an interior node, the left one at the last node).</summary>
        double Derivative(double x);
    }

    /// <summary>Piecewise-linear interpolation (<see cref="InterpolationKernels.LinearValue"/>). Local.</summary>
    public sealed class LinearInterpolator : IInterpolator
    {
        /// <summary>The shared instance.</summary>
        public static readonly LinearInterpolator Instance = new();

        private LinearInterpolator() { }

        /// <inheritdoc />
        public string Name => "Linear";

        /// <inheritdoc />
        public bool IsLocal => true;

        /// <inheritdoc />
        public IInterpolation Interpolate(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
        {
            InterpolationNodes.Validate(x, y);
            return new Interpolant(x.ToArray(), y.ToArray());
        }

        /// <inheritdoc />
        public override string ToString() => Name;

        private sealed class Interpolant : IInterpolation
        {
            private readonly double[] _x;
            private readonly double[] _y;

            public Interpolant(double[] x, double[] y)
            {
                _x = x;
                _y = y;
            }

            public double Value(double x) => InterpolationKernels.LinearValue(_x, _y, _x.Length, x);

            public double Derivative(double x) => InterpolationKernels.LinearDerivative(_x, _y, _x.Length, x);
        }
    }

    /// <summary>
    /// Monotonicity-preserving cubic Hermite interpolation: Bessel (parabolic, three-point) node slopes passed through
    /// the Hyman (1983) filter, see <see cref="InterpolationKernels.MonotoneCubicSlopes"/>. The interpolant is
    /// continuously differentiable, exact at the nodes, reproduces quadratics where the filter is inactive, is monotone
    /// on each segment (no overshoot between nodes) and monotone overall on monotone data. Not local: a slope depends
    /// on the neighbouring nodes, so a segment depends on four nodes.
    /// </summary>
    public sealed class MonotoneCubicInterpolator : IInterpolator
    {
        /// <summary>The shared instance.</summary>
        public static readonly MonotoneCubicInterpolator Instance = new();

        private MonotoneCubicInterpolator() { }

        /// <inheritdoc />
        public string Name => "Monotone cubic (Bessel slopes, Hyman filter)";

        /// <inheritdoc />
        public bool IsLocal => false;

        /// <inheritdoc />
        public IInterpolation Interpolate(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
        {
            InterpolationNodes.Validate(x, y);
            var xs = x.ToArray();
            var ys = y.ToArray();
            var slopes = new double[xs.Length];
            InterpolationKernels.MonotoneCubicSlopes(xs, ys, xs.Length, slopes);
            return new Interpolant(xs, ys, slopes);
        }

        /// <inheritdoc />
        public override string ToString() => Name;

        private sealed class Interpolant : IInterpolation
        {
            private readonly double[] _x;
            private readonly double[] _y;
            private readonly double[] _slopes;

            public Interpolant(double[] x, double[] y, double[] slopes)
            {
                _x = x;
                _y = y;
                _slopes = slopes;
            }

            public double Value(double x) => InterpolationKernels.HermiteValue(_x, _y, _slopes, _x.Length, x);

            public double Derivative(double x) => InterpolationKernels.HermiteDerivative(_x, _y, _slopes, _x.Length, x);
        }
    }

    internal static class InterpolationNodes
    {
        public static void Validate(ReadOnlySpan<double> x, ReadOnlySpan<double> y)
        {
            if (x.Length == 0)
                throw new ArgumentException("At least one node is needed.", nameof(x));
            if (x.Length != y.Length)
                throw new ArgumentException($"{x.Length} abscissas but {y.Length} ordinates.", nameof(y));
            for (var i = 0; i < x.Length; i++)
            {
                if (!double.IsFinite(x[i]) || !double.IsFinite(y[i]))
                    throw new ArgumentException($"Node {i} ({x[i]}, {y[i]}) is not finite.", nameof(x));
                if (i > 0 && !(x[i] > x[i - 1]))
                    throw new ArgumentException($"Abscissas must be strictly increasing (x[{i - 1}] = {x[i - 1]}, x[{i}] = {x[i]}).", nameof(x));
            }
        }
    }
}
