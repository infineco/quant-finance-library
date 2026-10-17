using System;

namespace InFine.QuantFinance.Numerics
{
    /// <summary>
    /// A discount curve reduced to the flat data a kernel reads: nodes of an interpolated quantity, a mode
    /// (see <see cref="DiscountCurveKernels"/>), node slopes and the flat-forward extrapolation beyond the last node. Times
    /// are year fractions from the valuation date on the simulation clock. Immutable; validated once here so that the
    /// kernels need no check. Evaluation goes through <see cref="DiscountCurveKernels"/>, exactly as on the device.
    /// </summary>
    public sealed class CompiledDiscountCurve
    {
        private readonly double[] _times;
        private readonly double[] _values;
        private readonly double[] _slopes;

        /// <summary>Creates the curve, copying the arrays.</summary>
        /// <param name="mode">One of the <see cref="DiscountCurveKernels"/> mode constants.</param>
        /// <param name="times">Node times, finite, non-negative and strictly increasing; a zero-rate mode needs them positive.</param>
        /// <param name="values">Node values of the interpolated quantity, finite.</param>
        /// <param name="slopes">Node slopes, read by the Hermite modes only; <c>null</c> for the linear modes.</param>
        /// <param name="lastLogDiscount">ln P(0, T) at the last node time T.</param>
        /// <param name="lastForward">The instantaneous forward used beyond the last node.</param>
        /// <exception cref="ArgumentException">Any of the conditions above is not met.</exception>
        public CompiledDiscountCurve(int mode, ReadOnlySpan<double> times, ReadOnlySpan<double> values, ReadOnlySpan<double> slopes,
            double lastLogDiscount, double lastForward)
        {
            if (mode < DiscountCurveKernels.LinearZeroRate || mode > DiscountCurveKernels.HermiteLogDiscount)
                throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown curve mode.");
            if (times.Length == 0)
                throw new ArgumentException("A curve needs at least one node.", nameof(times));
            if (values.Length != times.Length)
                throw new ArgumentException($"{times.Length} node times but {values.Length} values.", nameof(values));
            var hermite = (mode & 2) != 0;
            if (hermite && slopes.Length != times.Length)
                throw new ArgumentException($"{times.Length} node times but {slopes.Length} slopes.", nameof(slopes));
            var zeroRate = (mode & 1) == 0;
            if (!zeroRate && (times[0] != 0.0 || values[0] != 0.0))
                throw new ArgumentException("A log-discount curve starts with the node (0, 0), i.e. P(0, 0) = 1.", nameof(times));
            for (var i = 0; i < times.Length; i++)
            {
                var increasing = i == 0 ? (zeroRate ? times[0] > 0.0 : true) : times[i] > times[i - 1];
                if (!double.IsFinite(times[i]) || !increasing)
                    throw new ArgumentException($"Node times must be finite, strictly increasing and positive for zero rates (node {i}: {times[i]}).", nameof(times));
                if (!double.IsFinite(values[i]) || (hermite && !double.IsFinite(slopes[i])))
                    throw new ArgumentException($"Node {i} is not finite.", nameof(values));
            }
            if (!double.IsFinite(lastLogDiscount) || !double.IsFinite(lastForward))
                throw new ArgumentException("The extrapolation parameters must be finite.", nameof(lastForward));

            Mode = mode;
            _times = times.ToArray();
            _values = values.ToArray();
            _slopes = hermite ? slopes.ToArray() : new double[times.Length];
            LastLogDiscount = lastLogDiscount;
            LastForward = lastForward;
        }

        /// <summary>The <see cref="DiscountCurveKernels"/> mode.</summary>
        public int Mode { get; }

        /// <summary>The number of nodes.</summary>
        public int Count => _times.Length;

        /// <summary>The node times.</summary>
        public ReadOnlySpan<double> Times => _times;

        /// <summary>The node values of the interpolated quantity.</summary>
        public ReadOnlySpan<double> Values => _values;

        /// <summary>The node slopes (zeros for the linear modes).</summary>
        public ReadOnlySpan<double> Slopes => _slopes;

        /// <summary>ln P(0, T) at the last node time T.</summary>
        public double LastLogDiscount { get; }

        /// <summary>The instantaneous forward beyond the last node.</summary>
        public double LastForward { get; }

        /// <summary><c>ln P(0, t)</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> is negative or not finite.</exception>
        public double LogDiscountFactor(double t)
        {
            CheckTime(t);
            return DiscountCurveKernels.LogDiscountFactor(Mode, _times, _values, _slopes, _times.Length, LastLogDiscount, LastForward, t);
        }

        /// <summary><c>P(0, t)</c>.</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> is negative or not finite.</exception>
        public double DiscountFactor(double t) => Math.Exp(LogDiscountFactor(t));

        /// <summary>The instantaneous forward <c>f(t)</c> (see <see cref="DiscountCurveKernels.InstantaneousForward"/>).</summary>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="t"/> is negative or not finite.</exception>
        public double InstantaneousForward(double t)
        {
            CheckTime(t);
            return DiscountCurveKernels.InstantaneousForward(Mode, _times, _values, _slopes, _times.Length, LastForward, t);
        }

        /// <summary>
        /// The continuously compounded forward over each step of a time grid,
        /// <c>r_k = (ln P(0, t_k) - ln P(0, t_{k+1})) / (t_{k+1} - t_k)</c>, one per step. A log-Euler diffusion using
        /// <c>r_k</c> as drift on <c>[t_k, t_{k+1}]</c> has <c>E[S_{k+1} | S_k] = S_k P(0, t_k) / P(0, t_{k+1})</c>, so the
        /// discounted spot <c>P(0, t_k) S_k</c> is exactly a martingale on the grid, whatever the curve shape between nodes.
        /// </summary>
        /// <exception cref="ArgumentException">Fewer than two times, or times not finite, non-negative and strictly increasing.</exception>
        public double[] StepForwards(ReadOnlySpan<double> gridTimes)
        {
            if (gridTimes.Length < 2)
                throw new ArgumentException("A time grid needs at least two times.", nameof(gridTimes));
            var logDiscounts = LogDiscountFactors(gridTimes);
            var rates = new double[gridTimes.Length - 1];
            for (var k = 0; k < rates.Length; k++)
                rates[k] = (logDiscounts[k] - logDiscounts[k + 1]) / (gridTimes[k + 1] - gridTimes[k]);
            return rates;
        }

        /// <summary>The discount factors <c>P(0, t_k)</c> on a time grid.</summary>
        /// <exception cref="ArgumentException">Times not finite, non-negative and strictly increasing.</exception>
        public double[] DiscountFactors(ReadOnlySpan<double> gridTimes)
        {
            var result = LogDiscountFactors(gridTimes);
            for (var k = 0; k < result.Length; k++)
                result[k] = Math.Exp(result[k]);
            return result;
        }

        private double[] LogDiscountFactors(ReadOnlySpan<double> gridTimes)
        {
            var result = new double[gridTimes.Length];
            for (var k = 0; k < gridTimes.Length; k++)
            {
                if (!double.IsFinite(gridTimes[k]) || gridTimes[k] < 0.0 || (k > 0 && !(gridTimes[k] > gridTimes[k - 1])))
                    throw new ArgumentException($"Grid times must be finite, non-negative and strictly increasing (index {k}: {gridTimes[k]}).", nameof(gridTimes));
                result[k] = LogDiscountFactor(gridTimes[k]);
            }
            return result;
        }

        private static void CheckTime(double t)
        {
            if (!(t >= 0.0) || double.IsInfinity(t))
                throw new ArgumentOutOfRangeException(nameof(t), t, "Curve times must be finite and non-negative.");
        }
    }
}
