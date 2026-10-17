using System;
using System.Collections.Generic;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.Volatility
{
    /// <summary>How quotes are weighted in a least-squares fit of total variance.</summary>
    public enum SmileWeighting
    {
        /// <summary>The weights of the <see cref="SmileQuotes"/> as given (1 when none were given).</summary>
        AsGiven,

        /// <summary>
        /// The given weights times the Black vega <c>dC/dsigma</c> of each quote, normalised so that the largest vega of
        /// the slice is 1. This de-emphasises the far wings, whose vols carry little price information.
        /// </summary>
        Vega,
    }

    /// <summary>
    /// Market implied volatilities of one expiry: maturity, forward, strikes, vols and optional least-squares weights.
    /// Fits work in total variance <c>w = sigma^2 T</c> against log-forward moneyness <c>k = ln(K / F)</c>.
    /// </summary>
    public sealed class SmileQuotes
    {
        readonly double[] _strikes, _vols, _weights;

        /// <summary>Creates the quotes; arrays are copied.</summary>
        /// <param name="maturity">time to expiry in years, positive.</param>
        /// <param name="forward">forward of the underlying for the expiry, positive.</param>
        /// <param name="strikes">strikes, positive and distinct.</param>
        /// <param name="volatilities">Black implied volatilities, positive.</param>
        /// <param name="weights">non-negative least-squares weights (on squared total-variance errors), not all zero;
        /// null for 1 everywhere.</param>
        public SmileQuotes(double maturity, double forward, IReadOnlyList<double> strikes,
            IReadOnlyList<double> volatilities, IReadOnlyList<double>? weights = null)
        {
            ArgumentNullException.ThrowIfNull(strikes);
            ArgumentNullException.ThrowIfNull(volatilities);
            RequirePositive(maturity, nameof(maturity));
            RequirePositive(forward, nameof(forward));
            int n = strikes.Count;
            if (n == 0) throw new ArgumentException("At least one quote is needed.", nameof(strikes));
            if (volatilities.Count != n) throw new ArgumentException("There must be one volatility per strike.", nameof(volatilities));
            if (weights != null && weights.Count != n) throw new ArgumentException("There must be one weight per strike.", nameof(weights));

            _strikes = new double[n]; _vols = new double[n]; _weights = new double[n];
            double weightSum = 0.0;
            for (int i = 0; i < n; i++)
            {
                RequirePositive(strikes[i], $"strikes[{i}]");
                RequirePositive(volatilities[i], $"volatilities[{i}]");
                double weight = weights == null ? 1.0 : weights[i];
                if (!(weight >= 0.0) || double.IsInfinity(weight))
                    throw new ArgumentOutOfRangeException(nameof(weights), weight, $"weights[{i}] must be finite and non-negative.");
                _strikes[i] = strikes[i]; _vols[i] = volatilities[i]; _weights[i] = weight;
                weightSum += weight;
            }
            if (!(weightSum > 0.0)) throw new ArgumentException("At least one weight must be positive.", nameof(weights));
            var sorted = (double[])_strikes.Clone();
            Array.Sort(sorted);
            for (int i = 1; i < n; i++)
                if (sorted[i] == sorted[i - 1]) throw new ArgumentException($"Strike {sorted[i]} is quoted twice.", nameof(strikes));

            Maturity = maturity;
            Forward = forward;
        }

        /// <summary>
        /// Quotes from discounted Black-76 option prices, converted to implied volatilities with
        /// <see cref="ImpliedVolatility.Black76(double, double, double, double, double, OptionType)"/>.
        /// </summary>
        /// <exception cref="ArgumentException">A price has no positive finite implied volatility (outside the
        /// arbitrage bounds, or at intrinsic value).</exception>
        public static SmileQuotes FromPrices(double maturity, double forward, double discountFactor,
            IReadOnlyList<double> strikes, IReadOnlyList<double> prices, IReadOnlyList<OptionType> types,
            IReadOnlyList<double>? weights = null)
        {
            ArgumentNullException.ThrowIfNull(strikes);
            ArgumentNullException.ThrowIfNull(prices);
            ArgumentNullException.ThrowIfNull(types);
            int n = strikes.Count;
            if (prices.Count != n || types.Count != n)
                throw new ArgumentException("There must be one price and one option type per strike.");
            var vols = new double[n];
            for (int i = 0; i < n; i++)
            {
                double vol = ImpliedVolatility.Black76(forward, strikes[i], maturity, discountFactor, prices[i], types[i]);
                if (!(vol > 0.0) || double.IsInfinity(vol))
                    throw new ArgumentException(
                        $"Price {prices[i]:R} of the {types[i]} struck at {strikes[i]:R} has no positive finite implied volatility ({vol:R}).",
                        nameof(prices));
                vols[i] = vol;
            }
            return new SmileQuotes(maturity, forward, strikes, vols, weights);
        }

        /// <summary>Time to expiry in years.</summary>
        public double Maturity { get; }

        /// <summary>Forward for the expiry.</summary>
        public double Forward { get; }

        /// <summary>Number of quotes.</summary>
        public int Count => _strikes.Length;

        /// <summary>Strikes, in the order given.</summary>
        public IReadOnlyList<double> Strikes => _strikes;

        /// <summary>Implied volatilities.</summary>
        public IReadOnlyList<double> Volatilities => _vols;

        /// <summary>Least-squares weights as given.</summary>
        public IReadOnlyList<double> Weights => _weights;

        /// <summary>Log-forward moneyness <c>ln(K / F)</c> of quote <paramref name="i"/>.</summary>
        public double LogMoneyness(int i) => Math.Log(_strikes[i] / Forward);

        /// <summary>Market total variance <c>sigma_i^2 T</c> of quote <paramref name="i"/>.</summary>
        public double TotalVariance(int i) => _vols[i] * _vols[i] * Maturity;

        /// <summary>The weights applied by a fit under <paramref name="weighting"/>.</summary>
        public double[] EffectiveWeights(SmileWeighting weighting)
        {
            var result = (double[])_weights.Clone();
            if (weighting == SmileWeighting.AsGiven) return result;
            if (weighting != SmileWeighting.Vega)
                throw new ArgumentOutOfRangeException(nameof(weighting), weighting, "Unknown weighting.");
            double sqrtT = Math.Sqrt(Maturity), maxVega = 0.0;
            var vega = new double[Count];
            for (int i = 0; i < Count; i++)
            {
                vega[i] = Black76Kernels.NormalisedVega(Forward, _strikes[i], _vols[i] * sqrtT) * sqrtT;
                maxVega = Math.Max(maxVega, vega[i]);
            }
            for (int i = 0; i < Count; i++) result[i] *= maxVega > 0.0 ? vega[i] / maxVega : 1.0;
            return result;
        }

        /// <summary>Total variance at <c>k = 0</c> by linear interpolation in <c>k</c> between the quotes that bracket
        /// it (the nearest quote when all lie on one side).</summary>
        internal double AtTheMoneyTotalVariance()
        {
            double kBelow = double.NegativeInfinity, wBelow = double.NaN, kAbove = double.PositiveInfinity, wAbove = double.NaN;
            for (int i = 0; i < Count; i++)
            {
                double k = LogMoneyness(i);
                if (k <= 0.0 && k > kBelow) { kBelow = k; wBelow = TotalVariance(i); }
                if (k >= 0.0 && k < kAbove) { kAbove = k; wAbove = TotalVariance(i); }
            }
            if (double.IsNaN(wBelow)) return wAbove;
            if (double.IsNaN(wAbove) || kAbove == kBelow) return wBelow;
            return wBelow + (wAbove - wBelow) * (0.0 - kBelow) / (kAbove - kBelow);
        }

        static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite and positive.");
        }
    }
}
