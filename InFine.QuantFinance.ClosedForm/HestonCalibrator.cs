using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// One market quote for <see cref="HestonCalibrator"/>: a European option on the forward of its expiry, quoted as
    /// a Black-76 implied volatility (use <see cref="FromPrice"/> for a price quote).
    /// </summary>
    /// <param name="Maturity">time to expiry in years, positive.</param>
    /// <param name="Forward">forward of the underlying for the expiry, positive.</param>
    /// <param name="DiscountFactor">discount factor to the payment date, positive.</param>
    /// <param name="Strike">strike, positive.</param>
    /// <param name="Volatility">Black-76 implied volatility, positive.</param>
    /// <param name="Weight">residual weight, non-negative (1 by default).</param>
    public readonly record struct HestonQuote(double Maturity, double Forward, double DiscountFactor, double Strike,
        double Volatility, double Weight = 1.0)
    {
        /// <summary>
        /// Quote from a discounted option price, converted once to its Black-76 implied volatility (calls and puts of
        /// the same strike carry the same information). Throws if the price has no positive implied volatility.
        /// </summary>
        public static HestonQuote FromPrice(double maturity, double forward, double discountFactor, double strike,
            double price, OptionType type, double weight = 1.0)
        {
            double vol = ImpliedVolatility.Black76(forward, strike, maturity, discountFactor, price, type);
            if (!(vol > 0.0) || double.IsInfinity(vol))
                throw new ArgumentOutOfRangeException(nameof(price), price,
                    "price has no positive finite implied volatility (outside the arbitrage bounds or no time value).");
            return new HestonQuote(maturity, forward, discountFactor, strike, vol, weight);
        }
    }

    /// <summary>What the calibration residuals measure.</summary>
    public enum HestonCalibrationObjective
    {
        /// <summary>
        /// <c>w (sigma_model - sigma_market)</c>, the model price inverted to a Black-76 implied volatility
        /// ("Let's Be Rational", machine precision). The usual market objective: every quote counts in vol points.
        /// </summary>
        ImpliedVolatility,

        /// <summary>
        /// <c>w (P_model - P_market) / vega_market</c>, with the Black-76 vega at the market volatility floored at 1e-3
        /// of the at-the-money vega <c>DF F sqrt(T) / sqrt(2 pi)</c>. A first-order approximation of the
        /// implied-volatility objective that skips the per-residual inversion; it diverges from it for quotes far
        /// out of the money, where the price is no longer linear in the volatility.
        /// </summary>
        VegaWeightedPrice,
    }

    /// <summary>Settings of <see cref="HestonCalibrator.Calibrate"/>.</summary>
    public sealed class HestonCalibrationOptions
    {
        /// <summary>Residual definition. Default <see cref="HestonCalibrationObjective.ImpliedVolatility"/>.</summary>
        public HestonCalibrationObjective Objective { get; init; } = HestonCalibrationObjective.ImpliedVolatility;

        /// <summary>
        /// Starting point, or null for the default: v0 and theta from the squared volatility of the quote closest to
        /// the money at the shortest and at the longest maturity, kappa = 1, xi = 0.5, rho = -0.5 (projected onto the
        /// bounds).
        /// </summary>
        public HestonParameters? InitialGuess { get; init; }

        /// <summary>Lower bounds, componentwise. Default (1e-6, 1e-3, 1e-6, 1e-3, -0.999).</summary>
        public HestonParameters LowerBounds { get; init; } = new HestonParameters(1e-6, 1e-3, 1e-6, 1e-3, -0.999);

        /// <summary>Upper bounds, componentwise. Default (4, 50, 4, 10, 0.999).</summary>
        public HestonParameters UpperBounds { get; init; } = new HestonParameters(4.0, 50.0, 4.0, 10.0, 0.999);

        /// <summary>
        /// Adds the soft constraint residual <c>FellerPenaltyWeight max(0, xi^2 - 2 kappa theta)</c>, which pulls the
        /// solution towards the Feller condition without enforcing it. Off by default.
        /// </summary>
        public bool FellerPenalty { get; init; }

        /// <summary>Weight of the Feller residual, in the units of the objective per unit of variance^2. Default 1.</summary>
        public double FellerPenaltyWeight { get; init; } = 1.0;

        /// <summary>Number of COS terms per price. Default <see cref="HestonKernels.DefaultTerms"/>.</summary>
        public int Terms { get; init; } = HestonKernels.DefaultTerms;

        /// <summary>COS truncation width L. Default <see cref="HestonKernels.DefaultTruncationWidth"/>.</summary>
        public double TruncationWidth { get; init; } = HestonKernels.DefaultTruncationWidth;

        /// <summary>
        /// Solver tolerances, limits and finite-difference settings, or null for the solver defaults. Its bounds must
        /// be left unset: the bounds are <see cref="LowerBounds"/> and <see cref="UpperBounds"/>.
        /// </summary>
        public LevenbergMarquardtOptions Solver { get; init; }
    }

    /// <summary>Outcome of <see cref="HestonCalibrator.Calibrate"/>.</summary>
    public sealed class HestonCalibrationResult
    {
        internal HestonCalibrationResult(HestonParameters parameters, double[] residuals, double rmse,
            LevenbergMarquardtResult solver)
        {
            Parameters = parameters;
            Residuals = residuals;
            RootMeanSquareError = rmse;
            Solver = solver;
        }

        /// <summary>Calibrated parameters.</summary>
        public HestonParameters Parameters { get; }

        /// <summary>Weighted residual of each quote, in the order given, in objective units (vol, or price / vega).</summary>
        public double[] Residuals { get; }

        /// <summary>Root mean square of <see cref="Residuals"/> (the Feller residual excluded).</summary>
        public double RootMeanSquareError { get; }

        /// <summary>Raw solver result (status, iterations, Jacobian at the solution).</summary>
        public LevenbergMarquardtResult Solver { get; }

        /// <summary>True unless the solver hit an iteration or evaluation limit.</summary>
        public bool Converged => Solver.Converged;
    }

    /// <summary>
    /// Host-side Heston calibration: fits (v0, kappa, theta, xi, rho) to a set of European quotes across strikes
    /// and maturities by bounded Levenberg-Marquardt (<see cref="LevenbergMarquardt"/>), pricing with the COS method.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every quote is priced as the out-of-the-money option (put below the forward, call at or above it): the
    /// out-of-the-money price is all time value, so its implied volatility is best conditioned. The Jacobian is
    /// by finite differences (the solver's scheme, forward by default); COS prices are smooth in the parameters, the
    /// truncation range included, so finite differences are reliable.
    /// </para>
    /// <para>
    /// Heston calibration is a local, non-convex problem: kappa and xi are only weakly identified from a few
    /// maturities and trade off against each other, so the result depends on the initial guess when the data are
    /// noisy or sparse. Supply several maturities (the term structure pins kappa and theta) and a sensible
    /// <see cref="HestonCalibrationOptions.InitialGuess"/> for production use.
    /// </para>
    /// </remarks>
    public static class HestonCalibrator
    {
        /// <summary>Fits the Heston parameters to <paramref name="quotes"/> (at least five).</summary>
        /// <exception cref="ArgumentException">Invalid quotes or options, or non-finite residuals at the initial guess.</exception>
        public static HestonCalibrationResult Calibrate(IReadOnlyList<HestonQuote> quotes, HestonCalibrationOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(quotes);
            options ??= new HestonCalibrationOptions();
            int m = quotes.Count;
            if (m < 5) throw new ArgumentException("At least five quotes are needed for five parameters.", nameof(quotes));
            if (options.Terms < 1)
                throw new ArgumentOutOfRangeException(nameof(options), options.Terms, "Terms must be at least 1.");
            if (!(options.TruncationWidth > 0.0) || double.IsInfinity(options.TruncationWidth))
                throw new ArgumentOutOfRangeException(nameof(options), options.TruncationWidth, "TruncationWidth must be finite and positive.");
            if (options.FellerPenalty && (!(options.FellerPenaltyWeight >= 0.0) || double.IsInfinity(options.FellerPenaltyWeight)))
                throw new ArgumentOutOfRangeException(nameof(options), options.FellerPenaltyWeight, "FellerPenaltyWeight must be finite and non-negative.");
            if (options.Solver != null && (options.Solver.LowerBounds != null || options.Solver.UpperBounds != null))
                throw new ArgumentException("Set the bounds through HestonCalibrationOptions, not the solver options.", nameof(options));

            double[] lower = ToArray(options.LowerBounds), upper = ToArray(options.UpperBounds);
            options.LowerBounds.Validate();
            options.UpperBounds.Validate();
            for (int j = 0; j < 5; j++)
                if (!(lower[j] <= upper[j]) || double.IsInfinity(upper[j]))
                    throw new ArgumentException("Each lower bound must not exceed its (finite) upper bound.", nameof(options));

            var sign = new int[m];
            var marketPrice = new double[m];
            var vega = new double[m];
            for (int i = 0; i < m; i++)
            {
                HestonQuote q = quotes[i];
                Validate(q, i);
                sign[i] = q.Strike >= q.Forward ? 1 : -1;
                marketPrice[i] = Black76Kernels.Price(sign[i], q.Forward, q.Strike, q.DiscountFactor, q.Maturity, q.Volatility);
                double sqrtT = Math.Sqrt(q.Maturity);
                double atmVega = q.DiscountFactor * q.Forward * sqrtT * NormalDistribution.Pdf(0.0);
                vega[i] = Math.Max(q.DiscountFactor * sqrtT
                    * Black76Kernels.NormalisedVega(q.Forward, q.Strike, q.Volatility * sqrtT), 1e-3 * atmVega);
            }

            int terms = options.Terms;
            double width = options.TruncationWidth;
            bool volObjective = options.Objective == HestonCalibrationObjective.ImpliedVolatility;
            bool feller = options.FellerPenalty;
            double fellerWeight = options.FellerPenaltyWeight;

            void Residuals(double[] p, double[] r)
            {
                for (int i = 0; i < m; i++)
                {
                    HestonQuote q = quotes[i];
                    double price = HestonKernels.Price(sign[i], q.Forward, q.Strike, q.DiscountFactor, q.Maturity,
                        p[0], p[1], p[2], p[3], p[4], terms, width);
                    if (volObjective)
                    {
                        // out of the money: the price is pure time value, so the implied vol is >= 0 (NaN never occurs)
                        double vol = ImpliedVolatilityKernels.Black76(sign[i], price, q.Forward, q.Strike,
                            q.DiscountFactor, q.Maturity);
                        r[i] = q.Weight * (vol - q.Volatility);
                    }
                    else
                    {
                        r[i] = q.Weight * (price - marketPrice[i]) / vega[i];
                    }
                }
                if (feller) r[m] = fellerWeight * Math.Max(0.0, p[3] * p[3] - 2.0 * p[1] * p[2]);
            }

            HestonParameters guess = options.InitialGuess ?? DefaultGuess(quotes);
            LevenbergMarquardtOptions solverOptions = WithBounds(options.Solver ?? new LevenbergMarquardtOptions(), lower, upper);
            LevenbergMarquardtResult fit = LevenbergMarquardt.Minimize(Residuals, ToArray(guess), feller ? m + 1 : m,
                null, solverOptions);

            double[] fitted = fit.Parameters;
            var residuals = new double[m];
            double sum = 0.0;
            for (int i = 0; i < m; i++)
            {
                residuals[i] = fit.Residuals[i];
                sum += residuals[i] * residuals[i];
            }
            return new HestonCalibrationResult(
                new HestonParameters(fitted[0], fitted[1], fitted[2], fitted[3], fitted[4]), residuals,
                Math.Sqrt(sum / m), fit);
        }

        static HestonParameters DefaultGuess(IReadOnlyList<HestonQuote> quotes)
        {
            double shortest = double.PositiveInfinity, longest = 0.0;
            for (int i = 0; i < quotes.Count; i++)
            {
                shortest = Math.Min(shortest, quotes[i].Maturity);
                longest = Math.Max(longest, quotes[i].Maturity);
            }
            return new HestonParameters(AtmVariance(quotes, shortest), 1.0, AtmVariance(quotes, longest), 0.5, -0.5);
        }

        /// <summary>Squared volatility of the quote closest to the money (in |ln K/F|) at the given maturity.</summary>
        static double AtmVariance(IReadOnlyList<HestonQuote> quotes, double maturity)
        {
            double best = double.PositiveInfinity, vol = 0.0;
            for (int i = 0; i < quotes.Count; i++)
            {
                HestonQuote q = quotes[i];
                if (q.Maturity != maturity) continue;
                double moneyness = Math.Abs(Math.Log(q.Strike / q.Forward));
                if (moneyness < best)
                {
                    best = moneyness;
                    vol = q.Volatility;
                }
            }
            return vol * vol;
        }

        static LevenbergMarquardtOptions WithBounds(LevenbergMarquardtOptions o, double[] lower, double[] upper)
        {
            return new LevenbergMarquardtOptions
            {
                FunctionTolerance = o.FunctionTolerance,
                ParameterTolerance = o.ParameterTolerance,
                GradientTolerance = o.GradientTolerance,
                MaxIterations = o.MaxIterations,
                MaxFunctionEvaluations = o.MaxFunctionEvaluations,
                InitialStepBound = o.InitialStepBound,
                ParameterScale = o.ParameterScale,
                FiniteDifference = o.FiniteDifference,
                FiniteDifferenceStep = o.FiniteDifferenceStep,
                LowerBounds = lower,
                UpperBounds = upper,
            };
        }

        static double[] ToArray(HestonParameters p)
        {
            return new[] { p.V0, p.Kappa, p.Theta, p.Xi, p.Rho };
        }

        static void Validate(HestonQuote q, int index)
        {
            string at = $"quotes[{index}]";
            if (!(q.Maturity > 0.0) || double.IsInfinity(q.Maturity)) throw new ArgumentException($"{at}: maturity must be finite and positive.");
            if (!(q.Forward > 0.0) || double.IsInfinity(q.Forward)) throw new ArgumentException($"{at}: forward must be finite and positive.");
            if (!(q.DiscountFactor > 0.0) || double.IsInfinity(q.DiscountFactor)) throw new ArgumentException($"{at}: discount factor must be finite and positive.");
            if (!(q.Strike > 0.0) || double.IsInfinity(q.Strike)) throw new ArgumentException($"{at}: strike must be finite and positive.");
            if (!(q.Volatility > 0.0) || double.IsInfinity(q.Volatility)) throw new ArgumentException($"{at}: volatility must be finite and positive.");
            if (!(q.Weight >= 0.0) || double.IsInfinity(q.Weight)) throw new ArgumentException($"{at}: weight must be finite and non-negative.");
        }
    }
}
