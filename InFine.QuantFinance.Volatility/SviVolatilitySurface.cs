using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// A surface of raw SVI slices at pillar expiries <c>T_1 &lt; ... &lt; T_n</c>, interpolated linearly in total
    /// variance at constant log-forward moneyness.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Between pillars: <c>w(t, k) = (1 - lambda) w_i(k) + lambda w_{i+1}(k)</c>,
    /// <c>lambda = (t - T_i) / (T_{i+1} - T_i)</c>. If the pillar slices are free of calendar arbitrage
    /// (<c>w_{i+1} &gt;= w_i</c>) so is every interpolated expiry, since <c>dw/dt = (w_{i+1} - w_i) / (T_{i+1} - T_i)</c>.
    /// Butterfly absence at the pillars is not inherited in general; check interpolated slices if it matters.</item>
    /// <item>Before the first pillar: <c>w(t, k) = (t / T_1) w_1(k)</c>, i.e. the implied volatility at fixed <c>k</c> is
    /// flat at its <c>T_1</c> value, and <c>w(0, k) = 0</c>.</item>
    /// <item>After the last pillar: <c>w(t, k) = (t / T_n) w_n(k)</c>, flat implied volatility at fixed <c>k</c>.</item>
    /// </list>
    /// Both extrapolations scale a slice by a positive factor, which keeps <c>w</c> increasing in <c>t</c>.
    /// </remarks>
    public sealed class SviVolatilitySurface : VolatilitySurface
    {
        readonly double[] _maturities;
        readonly SviRawParameters[] _slices;

        /// <summary>Creates the surface.</summary>
        /// <param name="forwards">forward curve defining the moneyness.</param>
        /// <param name="maturities">pillar expiries, positive and strictly increasing.</param>
        /// <param name="slices">one SVI slice per pillar.</param>
        public SviVolatilitySurface(IForwardCurve forwards, IReadOnlyList<double> maturities, IReadOnlyList<SviRawParameters> slices)
            : base(forwards)
        {
            ArgumentNullException.ThrowIfNull(maturities);
            ArgumentNullException.ThrowIfNull(slices);
            int n = maturities.Count;
            if (n == 0) throw new ArgumentException("At least one slice is needed.", nameof(maturities));
            if (slices.Count != n) throw new ArgumentException("There must be one slice per maturity.", nameof(slices));
            _maturities = new double[n];
            _slices = new SviRawParameters[n];
            for (int i = 0; i < n; i++)
            {
                double t = maturities[i];
                if (!(t > 0.0) || double.IsInfinity(t) || (i > 0 && !(t > _maturities[i - 1])))
                    throw new ArgumentException("Maturities must be finite, positive and strictly increasing.", nameof(maturities));
                if (slices[i].Sigma <= 0.0)
                    throw new ArgumentException($"Slice {i} is uninitialised.", nameof(slices));
                _maturities[i] = t;
                _slices[i] = slices[i];
            }
        }

        /// <summary>Builds the surface from per-expiry calibrations (any order).</summary>
        public static SviVolatilitySurface FromCalibrations(IForwardCurve forwards, IEnumerable<SviCalibrationResult> calibrations)
        {
            ArgumentNullException.ThrowIfNull(calibrations);
            var list = new List<SviCalibrationResult>(calibrations);
            list.Sort((x, y) => x.Maturity.CompareTo(y.Maturity));
            var maturities = new double[list.Count];
            var slices = new SviRawParameters[list.Count];
            for (int i = 0; i < list.Count; i++) { maturities[i] = list[i].Maturity; slices[i] = list[i].Parameters; }
            return new SviVolatilitySurface(forwards, maturities, slices);
        }

        /// <summary>Pillar expiries.</summary>
        public IReadOnlyList<double> Maturities => _maturities;

        /// <summary>Pillar slices.</summary>
        public IReadOnlyList<SviRawParameters> Slices => _slices;

        /// <inheritdoc />
        public override ITotalVarianceSlice Slice(double t)
        {
            RequireTime(t, allowZero: true);
            int n = _maturities.Length;
            if (t <= _maturities[0]) return new Blend(_slices[0], t / _maturities[0], _slices[0], 0.0);
            if (t >= _maturities[n - 1]) return new Blend(_slices[n - 1], t / _maturities[n - 1], _slices[n - 1], 0.0);
            int i = Array.BinarySearch(_maturities, t);
            if (i >= 0) return new Blend(_slices[i], 1.0, _slices[i], 0.0);
            int upper = ~i, lower = upper - 1;
            double lambda = (t - _maturities[lower]) / (_maturities[upper] - _maturities[lower]);
            return new Blend(_slices[lower], 1.0 - lambda, _slices[upper], lambda);
        }

        /// <summary>
        /// Calendar check between consecutive pillars on <paramref name="grid"/>; by the remarks this covers every
        /// expiry. Returns one result per pair (pillar i, pillar i + 1).
        /// </summary>
        public IReadOnlyList<CalendarCheckResult> CheckCalendar(IReadOnlyList<double> grid, double tolerance = 0.0)
        {
            var results = new CalendarCheckResult[_slices.Length - 1];
            for (int i = 0; i + 1 < _slices.Length; i++)
                results[i] = ArbitrageChecks.CheckCalendar(_slices[i], _slices[i + 1], grid, tolerance);
            return results;
        }

        /// <summary>alpha w_1 + beta w_2 with non-negative weights.</summary>
        readonly struct Blend : ITotalVarianceSlice
        {
            readonly SviRawParameters _first, _second;
            readonly double _alpha, _beta;

            public Blend(SviRawParameters first, double alpha, SviRawParameters second, double beta)
            {
                _first = first; _alpha = alpha; _second = second; _beta = beta;
            }

            public double TotalVariance(double k) =>
                _alpha * _first.TotalVariance(k) + (_beta == 0.0 ? 0.0 : _beta * _second.TotalVariance(k));

            public double TotalVarianceDerivative(double k) =>
                _alpha * _first.TotalVarianceDerivative(k) + (_beta == 0.0 ? 0.0 : _beta * _second.TotalVarianceDerivative(k));

            public double TotalVarianceSecondDerivative(double k) =>
                _alpha * _first.TotalVarianceSecondDerivative(k) + (_beta == 0.0 ? 0.0 : _beta * _second.TotalVarianceSecondDerivative(k));
        }
    }
}
