using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Monte Carlo estimate of a price and of its gradient with respect to the model inputs
    /// (<see cref="AdjointMonteCarlo"/>): means over the paths and their standard errors.
    /// </summary>
    public sealed class GradientEstimate
    {
        internal GradientEstimate(int paths, double price, double priceStandardError, double[] gradient, double[] gradientStandardError)
        {
            Paths = paths;
            Price = price;
            PriceStandardError = priceStandardError;
            Gradient = gradient;
            GradientStandardError = gradientStandardError;
        }

        /// <summary>Number of paths.</summary>
        public int Paths { get; }

        /// <summary>Mean discounted payoff.</summary>
        public double Price { get; }

        /// <summary>Standard error of <see cref="Price"/>.</summary>
        public double PriceStandardError { get; }

        /// <summary>
        /// Mean pathwise derivative of the discounted payoff with respect to each input, in the layout of the model's adjoint
        /// (<see cref="LogNormalAdjoint"/>, <see cref="CorrelatedLogNormalAdjoint"/>, <see cref="HestonAdjoint"/>).
        /// </summary>
        public double[] Gradient { get; }

        /// <summary>Standard error of each entry of <see cref="Gradient"/>.</summary>
        public double[] GradientStandardError { get; }
    }

    /// <summary>
    /// A payoff that <see cref="AdjointMonteCarlo"/> evaluates with its adjoint on each path: the host-side handle on an
    /// <see cref="AdjointGreeks"/> function and its parameters, validated once.
    /// </summary>
    public abstract class AdjointPayoff
    {
        AdjointPayoff(int assetCount)
        {
            AssetCount = assetCount;
        }

        /// <summary>Number of assets the payoff reads (1 for the single-asset payoffs).</summary>
        public int AssetCount { get; }

        /// <summary>European call (<paramref name="sign"/> +1) or put (-1) paid at the last pillar (<see cref="AdjointGreeks.European"/>).</summary>
        public static AdjointPayoff European(int sign, double strike)
        {
            CheckSignAndStrike(sign, strike);
            return new EuropeanPayoff(sign, strike);
        }

        /// <summary>
        /// Arithmetic average-price Asian call (+1) or put (-1) on the given fixing pillars, paid at the last pillar
        /// (<see cref="AdjointGreeks.ArithmeticAsian"/>).
        /// </summary>
        public static AdjointPayoff ArithmeticAsian(int sign, double strike, int[] fixings)
        {
            CheckSignAndStrike(sign, strike);
            if (fixings == null) throw new ArgumentNullException(nameof(fixings));
            if (fixings.Length == 0) throw new ArgumentException("At least one fixing is required.", nameof(fixings));
            return new AsianPayoff(sign, strike, (int[])fixings.Clone());
        }

        /// <summary>
        /// Arithmetic basket call (+1) or put (-1) on <c>sum_d w_d S_d(T) / reference_d</c>, paid at the last pillar
        /// (<see cref="AdjointGreeks.Basket"/>).
        /// </summary>
        public static AdjointPayoff Basket(int sign, double strike, double[] weights, double[] reference)
        {
            CheckSignAndStrike(sign, strike);
            if (weights == null) throw new ArgumentNullException(nameof(weights));
            MultiAssetMath.CheckAssets(weights.Length, reference, weights);
            return new BasketPayoff(sign, strike, (double[])weights.Clone(), (double[])reference.Clone());
        }

        /// <summary>
        /// Best-of (<paramref name="best"/>) or worst-of call (+1) or put (-1) on the performances
        /// <c>S_d(T) / reference_d</c>, paid at the last pillar (<see cref="AdjointGreeks.Rainbow"/>).
        /// </summary>
        public static AdjointPayoff Rainbow(int sign, bool best, double strike, double[] reference)
        {
            CheckSignAndStrike(sign, strike);
            if (reference == null) throw new ArgumentNullException(nameof(reference));
            MultiAssetMath.CheckAssets(reference.Length, reference, null);
            return new RainbowPayoff(sign, best, strike, (double[])reference.Clone());
        }

        /// <summary>Throws when a pillar the payoff reads is beyond <paramref name="steps"/>.</summary>
        internal virtual void CheckPillars(int steps)
        {
        }

        /// <summary>Discounted payoff of the log path and its direct log-spot adjoints (an <see cref="AdjointGreeks"/> function).</summary>
        internal abstract double Evaluate(double discountFactor, double[] logPath, int pathOffset, int steps,
            double[] logSpotAdjoint, int adjointOffset);

        static void CheckSignAndStrike(int sign, double strike)
        {
            if (sign != 1 && sign != -1) throw new ArgumentOutOfRangeException(nameof(sign), "The sign must be +1 (call) or -1 (put).");
            if (double.IsNaN(strike) || double.IsInfinity(strike)) throw new ArgumentOutOfRangeException(nameof(strike), "The strike must be finite.");
        }

        sealed class EuropeanPayoff : AdjointPayoff
        {
            readonly int _sign;
            readonly double _strike;

            internal EuropeanPayoff(int sign, double strike) : base(1)
            {
                _sign = sign;
                _strike = strike;
            }

            internal override double Evaluate(double discountFactor, double[] logPath, int pathOffset, int steps,
                double[] logSpotAdjoint, int adjointOffset)
            {
                return AdjointGreeks.European(_sign, _strike, discountFactor, logPath, pathOffset, steps, logSpotAdjoint, adjointOffset);
            }
        }

        sealed class AsianPayoff : AdjointPayoff
        {
            readonly int _sign;
            readonly double _strike;
            readonly int[] _fixings;

            internal AsianPayoff(int sign, double strike, int[] fixings) : base(1)
            {
                _sign = sign;
                _strike = strike;
                _fixings = fixings;
            }

            internal override void CheckPillars(int steps)
            {
                foreach (int pillar in _fixings)
                    if (pillar < 0 || pillar > steps)
                        throw new ArgumentOutOfRangeException("fixings", $"Fixing pillar {pillar} is outside [0, {steps}].");
            }

            internal override double Evaluate(double discountFactor, double[] logPath, int pathOffset, int steps,
                double[] logSpotAdjoint, int adjointOffset)
            {
                return AdjointGreeks.ArithmeticAsian(_sign, _strike, _fixings, _fixings.Length, discountFactor, logPath, pathOffset, steps,
                    logSpotAdjoint, adjointOffset);
            }
        }

        sealed class BasketPayoff : AdjointPayoff
        {
            readonly int _sign;
            readonly double _strike;
            readonly double[] _weights, _reference;

            internal BasketPayoff(int sign, double strike, double[] weights, double[] reference) : base(weights.Length)
            {
                _sign = sign;
                _strike = strike;
                _weights = weights;
                _reference = reference;
            }

            internal override double Evaluate(double discountFactor, double[] logPath, int pathOffset, int steps,
                double[] logSpotAdjoint, int adjointOffset)
            {
                return AdjointGreeks.Basket(_sign, _strike, _weights, _reference, AssetCount, discountFactor, logPath, pathOffset, steps,
                    logSpotAdjoint, adjointOffset);
            }
        }

        sealed class RainbowPayoff : AdjointPayoff
        {
            readonly int _sign;
            readonly bool _best;
            readonly double _strike;
            readonly double[] _reference;

            internal RainbowPayoff(int sign, bool best, double strike, double[] reference) : base(reference.Length)
            {
                _sign = sign;
                _best = best;
                _strike = strike;
                _reference = reference;
            }

            internal override double Evaluate(double discountFactor, double[] logPath, int pathOffset, int steps,
                double[] logSpotAdjoint, int adjointOffset)
            {
                return AdjointGreeks.Rainbow(_sign, _best, _strike, _reference, AssetCount, discountFactor, logPath, pathOffset, steps,
                    logSpotAdjoint, adjointOffset);
            }
        }
    }

    /// <summary>
    /// Host driver of the adjoint (reverse-mode) pathwise Greeks: for each path, the forward sweep of the model, the payoff
    /// adjoint and the backward sweep (see <see cref="AdjointGreeks"/>), then means and standard errors of the price and of
    /// every sensitivity. The draws come from the caller, path by path, so the same generator (and sub-stream per path) can
    /// drive a bumped revaluation with common random numbers.
    /// </summary>
    /// <remarks>
    /// Single-threaded reference of what a kernel does per path with the same static cores; the scratch is allocated once
    /// per call. <c>normals(p, z)</c> must fill <c>z</c> with the draws of path p: <c>steps</c> of them for the
    /// single-asset log-normal model, <c>steps D</c> for the correlated model (draw of asset d on step t at
    /// <c>t * D + d</c>), <c>2 steps</c> for Heston (<c>z_S</c> of step k at <c>2 k</c>, <c>z_perp</c> at <c>2 k + 1</c>).
    /// </remarks>
    public static class AdjointMonteCarlo
    {
        /// <summary>
        /// Price and gradient under the single-asset log-normal model with a term structure (<see cref="LogNormalAdjoint"/>):
        /// delta, bucketed vegas, bucketed rhos and bucketed dividend sensitivities in one backward sweep per path.
        /// </summary>
        /// <param name="payoff">A single-asset payoff (<see cref="AdjointPayoff.European"/>, <see cref="AdjointPayoff.ArithmeticAsian"/>).</param>
        /// <param name="spot">Initial spot.</param>
        /// <param name="rate">Rate of each step (drift and discounting).</param>
        /// <param name="dividendYield">Dividend yield of each step.</param>
        /// <param name="sigma">Volatility of each step.</param>
        /// <param name="dt">Step lengths; the payoff is paid at their sum.</param>
        /// <param name="paths">Number of paths, at least 2.</param>
        /// <param name="normals">Fills the <c>steps</c> standard normal draws of a path.</param>
        public static GradientEstimate LogNormal(AdjointPayoff payoff, double spot, double[] rate, double[] dividendYield, double[] sigma,
            double[] dt, int paths, Action<int, double[]> normals)
        {
            int steps = CheckSteps(dt);
            CheckPayoff(payoff, 1, steps);
            CheckPositive(spot, nameof(spot));
            CheckFinite(rate, steps, nameof(rate));
            CheckFinite(dividendYield, steps, nameof(dividendYield));
            CheckVolatilities(sigma, steps);
            int n1 = steps + 1;
            var scratch = new double[2 * n1];
            return Run(paths, steps, LogNormalAdjoint.GradientLength(steps), normals, (z, gradient) =>
            {
                double df = LogNormalAdjoint.Forward(spot, rate, dividendYield, sigma, dt, steps, z, 0, scratch, 0);
                double premium = payoff.Evaluate(df, scratch, 0, steps, scratch, n1);
                LogNormalAdjoint.Reverse(spot, sigma, dt, steps, z, 0, premium, scratch, n1, gradient, 0);
                return premium;
            });
        }

        /// <summary>
        /// Price and gradient under the correlated multi-asset log-normal model (<see cref="CorrelatedLogNormalAdjoint"/>):
        /// the D deltas, D vegas, D dividend sensitivities and the bucketed rhos in one backward sweep per path.
        /// </summary>
        /// <param name="payoff">A payoff on <c>D</c> assets (<see cref="AdjointPayoff.Basket"/>, <see cref="AdjointPayoff.Rainbow"/>).</param>
        /// <param name="spot">Initial spots, D entries.</param>
        /// <param name="sigma">Volatilities, D entries.</param>
        /// <param name="dividendYield">Dividend yields, D entries.</param>
        /// <param name="correlation">Factorised correlation of dimension D.</param>
        /// <param name="stepRates">Rate of each step (drift and discounting).</param>
        /// <param name="dt">Step lengths; the payoff is paid at their sum.</param>
        /// <param name="paths">Number of paths, at least 2.</param>
        /// <param name="normals">Fills the <c>steps D</c> independent standard normal draws of a path.</param>
        public static GradientEstimate CorrelatedLogNormal(AdjointPayoff payoff, double[] spot, double[] sigma, double[] dividendYield,
            CholeskyCorrelation correlation, double[] stepRates, double[] dt, int paths, Action<int, double[]> normals)
        {
            if (correlation == null) throw new ArgumentNullException(nameof(correlation));
            int assets = correlation.Dimension;
            int steps = CheckSteps(dt);
            CheckPayoff(payoff, assets, steps);
            if (spot == null) throw new ArgumentNullException(nameof(spot));
            if (spot.Length != assets) throw new ArgumentException($"Expected {assets} spots.", nameof(spot));
            foreach (double s in spot) CheckPositive(s, nameof(spot));
            CheckVolatilities(sigma, assets);
            CheckFinite(dividendYield, assets, nameof(dividendYield));
            CheckFinite(stepRates, steps, nameof(stepRates));
            double[] lower = correlation.Lower;
            int pathLength = (steps + 1) * assets, correlatedOffset = pathLength, adjointOffset = pathLength + steps * assets;
            var scratch = new double[2 * pathLength + steps * assets];
            return Run(paths, steps * assets, CorrelatedLogNormalAdjoint.GradientLength(assets, steps), normals, (z, gradient) =>
            {
                double df = CorrelatedLogNormalAdjoint.Forward(spot, sigma, dividendYield, lower, assets, dt, stepRates, steps, z, 0,
                    scratch, 0, scratch, correlatedOffset);
                double premium = payoff.Evaluate(df, scratch, 0, steps, scratch, adjointOffset);
                CorrelatedLogNormalAdjoint.Reverse(spot, sigma, assets, dt, steps, scratch, correlatedOffset, premium,
                    scratch, adjointOffset, gradient, 0);
                return premium;
            });
        }

        /// <summary>
        /// Price and gradient under Heston with full-truncation Euler steps (<see cref="HestonAdjoint"/>): delta and the
        /// sensitivities to v_0, kappa, theta, xi, rho and the rate in one backward sweep per path.
        /// </summary>
        /// <param name="payoff">A single-asset payoff (<see cref="AdjointPayoff.European"/>, <see cref="AdjointPayoff.ArithmeticAsian"/>).</param>
        /// <param name="spot">Initial spot.</param>
        /// <param name="v0">Initial variance, non-negative.</param>
        /// <param name="kappa">Mean-reversion speed, non-negative.</param>
        /// <param name="theta">Long-term variance, non-negative.</param>
        /// <param name="xi">Volatility of variance, non-negative.</param>
        /// <param name="rho">Spot/variance correlation, in (-1, 1).</param>
        /// <param name="rate">Risk-free rate.</param>
        /// <param name="dt">Step lengths; the payoff is paid at their sum.</param>
        /// <param name="paths">Number of paths, at least 2.</param>
        /// <param name="normals">Fills the <c>2 steps</c> standard normal draws of a path.</param>
        public static GradientEstimate Heston(AdjointPayoff payoff, double spot, double v0, double kappa, double theta, double xi,
            double rho, double rate, double[] dt, int paths, Action<int, double[]> normals)
        {
            int steps = CheckSteps(dt);
            CheckPayoff(payoff, 1, steps);
            CheckPositive(spot, nameof(spot));
            CheckNonNegative(v0, nameof(v0));
            CheckNonNegative(kappa, nameof(kappa));
            CheckNonNegative(theta, nameof(theta));
            CheckNonNegative(xi, nameof(xi));
            if (!(Math.Abs(rho) < 1.0)) throw new ArgumentOutOfRangeException(nameof(rho), "The correlation must be in (-1, 1).");
            if (double.IsNaN(rate) || double.IsInfinity(rate)) throw new ArgumentOutOfRangeException(nameof(rate), "The rate must be finite.");
            int n1 = steps + 1;
            var scratch = new double[3 * n1];
            return Run(paths, 2 * steps, HestonAdjoint.GradientLength, normals, (z, gradient) =>
            {
                double df = HestonAdjoint.Forward(spot, v0, kappa, theta, xi, rho, rate, dt, steps, z, 0, scratch, 0, scratch, n1);
                double premium = payoff.Evaluate(df, scratch, 0, steps, scratch, 2 * n1);
                HestonAdjoint.Reverse(spot, kappa, theta, xi, rho, dt, steps, z, 0, scratch, n1, premium, scratch, 2 * n1, gradient, 0);
                return premium;
            });
        }

        /// <summary>Runs the paths and returns means and standard errors (Welford's running moments).</summary>
        static GradientEstimate Run(int paths, int normalCount, int gradientLength, Action<int, double[]> normals,
            Func<double[], double[], double> path)
        {
            if (paths < 2) throw new ArgumentOutOfRangeException(nameof(paths), "At least two paths are required.");
            if (normals == null) throw new ArgumentNullException(nameof(normals));
            var z = new double[normalCount];
            var gradient = new double[gradientLength];
            var mean = new double[gradientLength];
            var m2 = new double[gradientLength];
            double priceMean = 0.0, priceM2 = 0.0;
            for (int p = 0; p < paths; ++p)
            {
                normals(p, z);
                Array.Clear(gradient, 0, gradientLength);
                double premium = path(z, gradient);
                double n = p + 1;
                Accumulate(premium, n, ref priceMean, ref priceM2);
                for (int i = 0; i < gradientLength; ++i)
                    Accumulate(gradient[i], n, ref mean[i], ref m2[i]);
            }
            var error = new double[gradientLength];
            for (int i = 0; i < gradientLength; ++i)
                error[i] = Math.Sqrt(m2[i] / (paths - 1.0) / paths);
            return new GradientEstimate(paths, priceMean, Math.Sqrt(priceM2 / (paths - 1.0) / paths), mean, error);
        }

        static void Accumulate(double x, double n, ref double mean, ref double m2)
        {
            double delta = x - mean;
            mean += delta / n;
            m2 += delta * (x - mean);
        }

        static void CheckPayoff(AdjointPayoff payoff, int assets, int steps)
        {
            if (payoff == null) throw new ArgumentNullException(nameof(payoff));
            if (payoff.AssetCount != assets)
                throw new ArgumentException($"The payoff reads {payoff.AssetCount} assets, the model has {assets}.", nameof(payoff));
            payoff.CheckPillars(steps);
        }

        static int CheckSteps(double[] dt)
        {
            if (dt == null) throw new ArgumentNullException(nameof(dt));
            if (dt.Length == 0) throw new ArgumentException("At least one step is required.", nameof(dt));
            foreach (double h in dt)
                if (!(h > 0.0) || double.IsInfinity(h)) throw new ArgumentOutOfRangeException(nameof(dt), "Steps must be positive and finite.");
            return dt.Length;
        }

        static void CheckFinite(double[] values, int length, string name)
        {
            if (values == null) throw new ArgumentNullException(name);
            if (values.Length != length) throw new ArgumentException($"Expected {length} entries.", name);
            foreach (double x in values)
                if (double.IsNaN(x) || double.IsInfinity(x)) throw new ArgumentOutOfRangeException(name, "Entries must be finite.");
        }

        static void CheckVolatilities(double[] sigma, int length)
        {
            CheckFinite(sigma, length, nameof(sigma));
            foreach (double s in sigma)
                if (s < 0.0) throw new ArgumentOutOfRangeException(nameof(sigma), "Volatilities must be non-negative.");
        }

        static void CheckPositive(double x, string name)
        {
            if (!(x > 0.0) || double.IsInfinity(x)) throw new ArgumentOutOfRangeException(name, "Must be positive and finite.");
        }

        static void CheckNonNegative(double x, string name)
        {
            if (!(x >= 0.0) || double.IsInfinity(x)) throw new ArgumentOutOfRangeException(name, "Must be non-negative and finite.");
        }
    }
}
