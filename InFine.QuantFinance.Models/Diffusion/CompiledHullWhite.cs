using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.ClosedForm;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Numerics;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// A Hull-White model fitted to a compiled curve and reduced, on the host, to the flat arrays a kernel reads for one time
    /// grid: per step the exact transition coefficients (<see cref="HullWhiteStep.Coefficients"/>), per pillar
    /// <c>ln P(0, t_k)</c>, the drift <c>ln P(0, t_k) - V(t_k) / 2</c> of the log discount factor, <c>phi(t_k)</c>, and the curve
    /// nodes for evaluation off the grid. Read by <see cref="HullWhiteModel{Brownian}"/>, <see cref="HullWhiteDiscounting"/> and
    /// <see cref="HullWhiteRate"/>.
    /// <para>
    /// Stochastic discount factor: with <c>I(t) = int_0^t x ds</c> and <c>V(t) = Var I(t)</c>
    /// (<see cref="HullWhiteKernels.IntegralVariance"/>), <c>int_0^t phi ds = -ln P(0, t) + V(t) / 2</c>, so
    /// <c>D(0, t) = exp(-int_0^t r ds) = P(0, t) exp(-V(t) / 2 - I(t))</c> and <c>E[D(0, t)] = P(0, t)</c> exactly, on any grid
    /// and for any curve shape: the fit needs neither <c>theta(t)</c> nor a derivative of the forward curve.
    /// </para>
    /// </summary>
    public class CompiledHullWhite
    {
        double _a;
        double _sigma;
        int _stepCount;
        double[] _times;
        // per step
        double[] _decay;
        double[] _loading;
        double[] _l11;
        double[] _l21;
        double[] _l22;
        // per pillar
        double[] _logDiscount;
        double[] _logDrift;
        double[] _phi;
        // curve
        int _mode;
        int _count;
        double[] _curveTimes;
        double[] _curveValues;
        double[] _curveSlopes;
        double _lastLogDiscount;
        double _lastForward;

        /// <summary>Compiles the model on the pillars of <paramref name="schedule"/> (which must start at 0).</summary>
        public CompiledHullWhite(double meanReversion, double volatility, CompiledDiscountCurve curve, ISchedule schedule)
            : this(meanReversion, volatility, curve, ScheduleTimes.Of(CheckSchedule(schedule)))
        {
        }

        /// <summary>Compiles the model on a time grid.</summary>
        /// <param name="meanReversion">a, finite and positive.</param>
        /// <param name="volatility">sigma, finite and non-negative.</param>
        /// <param name="curve">The initial curve, on the clock of the grid.</param>
        /// <param name="gridTimes">Pillar times, starting at 0, strictly increasing; at least two.</param>
        public CompiledHullWhite(double meanReversion, double volatility, CompiledDiscountCurve curve, double[] gridTimes)
        {
            if (!(meanReversion > 0.0) || double.IsInfinity(meanReversion))
                throw new ArgumentOutOfRangeException(nameof(meanReversion), meanReversion, "The mean reversion must be finite and positive.");
            if (!(volatility >= 0.0) || double.IsInfinity(volatility))
                throw new ArgumentOutOfRangeException(nameof(volatility), volatility, "The volatility must be finite and non-negative.");
            if (curve == null) throw new ArgumentNullException(nameof(curve));
            if (gridTimes == null) throw new ArgumentNullException(nameof(gridTimes));
            if (gridTimes.Length < 2 || gridTimes[0] != 0.0)
                throw new ArgumentException("The grid needs at least two times and must start at 0 (the curve's reference date).", nameof(gridTimes));

            _a = meanReversion;
            _sigma = volatility;
            _stepCount = gridTimes.Length - 1;
            _times = (double[])gridTimes.Clone();
            _decay = new double[_stepCount];
            _loading = new double[_stepCount];
            _l11 = new double[_stepCount];
            _l21 = new double[_stepCount];
            _l22 = new double[_stepCount];
            for (int k = 0; k < _stepCount; ++k)
            {
                double dt = _times[k + 1] - _times[k];
                if (!(dt > 0.0) || double.IsInfinity(dt))
                    throw new ArgumentException($"Grid times must be finite and strictly increasing (index {k + 1}: {_times[k + 1]}).", nameof(gridTimes));
                HullWhiteStep.Coefficients(_a, _sigma, dt, out _decay[k], out _loading[k], out _l11[k], out _l21[k], out _l22[k]);
            }

            var discounts = curve.DiscountFactors(_times);
            _logDiscount = new double[_times.Length];
            _logDrift = new double[_times.Length];
            _phi = new double[_times.Length];
            for (int k = 0; k < _times.Length; ++k)
            {
                _logDiscount[k] = Math.Log(discounts[k]);
                _logDrift[k] = _logDiscount[k] - 0.5 * HullWhiteKernels.IntegralVariance(_a, _sigma, _times[k]);
                _phi[k] = HullWhiteKernels.Phi(_a, _sigma, curve.InstantaneousForward(_times[k]), _times[k]);
            }

            _mode = curve.Mode;
            _count = curve.Count;
            _curveTimes = curve.Times.ToArray();
            _curveValues = curve.Values.ToArray();
            _curveSlopes = curve.Slopes.ToArray();
            _lastLogDiscount = curve.LastLogDiscount;
            _lastForward = curve.LastForward;
        }

        /// <summary>a.</summary>
        public double MeanReversion { get { return _a; } }

        /// <summary>sigma.</summary>
        public double Volatility { get { return _sigma; } }

        /// <summary>Number of steps; pillars are <c>0 .. StepCount</c>.</summary>
        public int StepCount { get { return _stepCount; } }

        /// <summary>Time of pillar <paramref name="k"/>.</summary>
        [Kernel] public double Time(int k) { return _times[k]; }

        /// <summary>Length of step <paramref name="k"/>.</summary>
        [Kernel] public double StepLength(int k) { return _times[k + 1] - _times[k]; }

        /// <summary><c>ln P(0, t_k)</c> of the curve.</summary>
        [Kernel] public double LogDiscount(int k) { return _logDiscount[k]; }

        /// <summary><c>ln P(0, t_k) - V(t_k) / 2</c>: the log discount factor is this minus <c>I(t_k)</c>.</summary>
        [Kernel] public double LogDrift(int k) { return _logDrift[k]; }

        /// <summary><c>phi(t_k)</c>: the short rate at pillar <c>k</c> is the state plus this.</summary>
        [Kernel] public double PhiAt(int k) { return _phi[k]; }

        /// <summary>State at the end of step <paramref name="k"/>.</summary>
        [Kernel]
        public double NextState(int k, double x, double z1)
        {
            return HullWhiteStep.NextState(x, z1, _decay[k], _l11[k]);
        }

        /// <summary>Integral of the state over step <paramref name="k"/> (same draws as <see cref="NextState"/>).</summary>
        [Kernel]
        public double IntegralIncrement(int k, double x, double z1, double z2)
        {
            return HullWhiteStep.IntegralIncrement(x, z1, z2, _loading[k], _l21[k], _l22[k]);
        }

        /// <summary>The last pillar at or before <paramref name="t"/> (0 before the grid, the last pillar after it).</summary>
        [Kernel]
        public int PillarAtOrBefore(double t)
        {
            if (t >= _times[_stepCount]) return _stepCount;
            return InterpolationKernels.FindSegment(_times, _stepCount + 1, t);
        }

        /// <summary><c>ln P(0, t)</c> of the curve at any <c>t &gt;= 0</c>.</summary>
        [Kernel]
        public double CurveLogDiscount(double t)
        {
            return DiscountCurveKernels.LogDiscountFactor(_mode, _curveTimes, _curveValues, _curveSlopes, _count, _lastLogDiscount, _lastForward, t);
        }

        /// <summary><c>phi(t)</c> at any <c>t &gt;= 0</c>.</summary>
        [Kernel]
        public double Phi(double t)
        {
            return HullWhiteKernels.Phi(_a, _sigma,
                DiscountCurveKernels.InstantaneousForward(_mode, _curveTimes, _curveValues, _curveSlopes, _count, _lastForward, t), t);
        }

        /// <summary><c>ln P(t_k, T)</c> given the state <paramref name="x"/> at pillar <paramref name="k"/>, for <c>T &gt;= t_k</c>.</summary>
        [Kernel]
        public double LogZeroBond(int k, double x, double maturity)
        {
            return HullWhiteKernels.LogZeroBond(_a, _sigma, _logDiscount[k], CurveLogDiscount(maturity), _times[k], maturity, x);
        }

        /// <summary><c>E[r(t) | x(t_k) = x] = x e^{-a (t - t_k)} + phi(t)</c>, for <c>t &gt;= t_k</c>.</summary>
        [Kernel]
        public double ConditionalShortRate(int k, double x, double t)
        {
            return x * Math.Exp(-_a * (t - _times[k])) + Phi(t);
        }

        /// <summary>
        /// Host helper (plain arrays, no Hybridizer type): one path from the standard normals <paramref name="z1"/>,
        /// <paramref name="z2"/> (<see cref="StepCount"/> each), writing the state and <c>ln D(0, t_k)</c> at every pillar
        /// (<see cref="StepCount"/> + 1 each). Same arithmetic as <see cref="HullWhiteModel{Brownian}"/>.
        /// </summary>
        public void SimulatePath(double[] z1, double[] z2, double[] state, double[] logDiscount)
        {
            double x = 0.0, integral = 0.0;
            state[0] = 0.0;
            logDiscount[0] = _logDrift[0];
            for (int k = 0; k < _stepCount; ++k)
            {
                integral += IntegralIncrement(k, x, z1[k], z2[k]);
                x = NextState(k, x, z1[k]);
                state[k + 1] = x;
                logDiscount[k + 1] = _logDrift[k + 1] - integral;
            }
        }

        private static ISchedule CheckSchedule(ISchedule schedule)
        {
            if (schedule == null) throw new ArgumentNullException(nameof(schedule));
            return schedule;
        }
    }
}
