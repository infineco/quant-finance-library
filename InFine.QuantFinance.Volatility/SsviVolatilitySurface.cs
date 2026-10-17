using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// SSVI surface: <see cref="SsviParameters"/> plus the ATM total-variance curve <c>theta_t</c>, given at pillar
    /// expiries and interpolated linearly in <c>theta</c>.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item>Between pillars <c>theta_t</c> is linear in <c>t</c>; before the first pillar
    /// <c>theta_t = theta_1 t / T_1</c> and after the last <c>theta_t = theta_n t / T_n</c> (flat ATM implied
    /// volatility), with <c>theta_0 = 0</c>. Non-decreasing pillars therefore give a non-decreasing <c>theta_t</c>,
    /// which with the power law makes the surface free of calendar arbitrage (Theorem 4.1).</item>
    /// <item>Interpolating in <c>theta</c> rather than in total variance at fixed <c>k</c> keeps every expiry an exact
    /// SSVI slice, so the sufficient butterfly conditions apply between pillars too.</item>
    /// </list>
    /// </remarks>
    public sealed class SsviVolatilitySurface : VolatilitySurface
    {
        readonly double[] _maturities, _thetas;

        /// <summary>Creates the surface.</summary>
        /// <param name="forwards">forward curve defining the moneyness.</param>
        /// <param name="parameters">SSVI parameters shared by every expiry.</param>
        /// <param name="maturities">pillar expiries, positive and strictly increasing.</param>
        /// <param name="atTheMoneyTotalVariances">ATM total variance <c>theta</c> at each pillar, positive. A decreasing
        /// pair is accepted (it is market data) but reported by <see cref="IsAtTheMoneyVarianceNonDecreasing"/>.</param>
        public SsviVolatilitySurface(IForwardCurve forwards, SsviParameters parameters, IReadOnlyList<double> maturities,
            IReadOnlyList<double> atTheMoneyTotalVariances)
            : base(forwards)
        {
            ArgumentNullException.ThrowIfNull(maturities);
            ArgumentNullException.ThrowIfNull(atTheMoneyTotalVariances);
            if (!(parameters.Eta > 0.0)) throw new ArgumentException("The SSVI parameters are uninitialised.", nameof(parameters));
            int n = maturities.Count;
            if (n == 0) throw new ArgumentException("At least one pillar is needed.", nameof(maturities));
            if (atTheMoneyTotalVariances.Count != n)
                throw new ArgumentException("There must be one ATM total variance per maturity.", nameof(atTheMoneyTotalVariances));
            _maturities = new double[n];
            _thetas = new double[n];
            for (int i = 0; i < n; i++)
            {
                double t = maturities[i], theta = atTheMoneyTotalVariances[i];
                if (!(t > 0.0) || double.IsInfinity(t) || (i > 0 && !(t > _maturities[i - 1])))
                    throw new ArgumentException("Maturities must be finite, positive and strictly increasing.", nameof(maturities));
                if (!(theta > 0.0) || double.IsInfinity(theta))
                    throw new ArgumentOutOfRangeException(nameof(atTheMoneyTotalVariances), theta, "ATM total variances must be finite and positive.");
                _maturities[i] = t;
                _thetas[i] = theta;
            }
            Parameters = parameters;
        }

        /// <summary>The SSVI parameters.</summary>
        public SsviParameters Parameters { get; }

        /// <summary>Pillar expiries.</summary>
        public IReadOnlyList<double> Maturities => _maturities;

        /// <summary>ATM total variance at the pillars.</summary>
        public IReadOnlyList<double> AtTheMoneyTotalVariances => _thetas;

        /// <summary>True when the pillar ATM total variances never decrease (Theorem 4.1, condition 1).</summary>
        public bool IsAtTheMoneyVarianceNonDecreasing
        {
            get
            {
                for (int i = 1; i < _thetas.Length; i++) if (_thetas[i] < _thetas[i - 1]) return false;
                return true;
            }
        }

        /// <summary>
        /// The surface is free of static arbitrage by Gatheral-Jacquier's closed-form sufficient conditions:
        /// non-decreasing <c>theta_t</c> and the power-law condition of eq. (4.5).
        /// </summary>
        public bool SatisfiesSufficientNoArbitrageConditions =>
            IsAtTheMoneyVarianceNonDecreasing && Parameters.SatisfiesPowerLawNoArbitrageCondition;

        /// <summary>ATM total variance <c>theta_t</c> at any expiry (see the remarks for the interpolation).</summary>
        public double AtTheMoneyTotalVariance(double t)
        {
            RequireTime(t, allowZero: true);
            int n = _maturities.Length;
            if (t <= _maturities[0]) return _thetas[0] * t / _maturities[0];
            if (t >= _maturities[n - 1]) return _thetas[n - 1] * t / _maturities[n - 1];
            int i = Array.BinarySearch(_maturities, t);
            if (i >= 0) return _thetas[i];
            int upper = ~i, lower = upper - 1;
            double lambda = (t - _maturities[lower]) / (_maturities[upper] - _maturities[lower]);
            return _thetas[lower] + lambda * (_thetas[upper] - _thetas[lower]);
        }

        /// <inheritdoc />
        public override ITotalVarianceSlice Slice(double t)
        {
            double theta = AtTheMoneyTotalVariance(t);
            return theta > 0.0 ? Parameters.Slice(theta) : new SsviSlice(Parameters.Rho, 0.0, 0.0);
        }
    }
}
