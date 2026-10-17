using System;
using System.Collections.Generic;
using InFine.QuantFinance.Numerics;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// A calibration instrument: how the model prices it and its market price (positive). Build swaptions and caps with
    /// <see cref="Swaption"/> and <see cref="Cap"/>, or wrap any other model price.
    /// </summary>
    public sealed class HullWhiteCalibrationTarget
    {
        /// <summary>Creates a target.</summary>
        public HullWhiteCalibrationTarget(Func<HullWhite, double> modelPrice, double marketPrice)
        {
            ModelPrice = modelPrice ?? throw new ArgumentNullException(nameof(modelPrice));
            if (!(marketPrice > 0.0) || double.IsInfinity(marketPrice))
                throw new ArgumentOutOfRangeException(nameof(marketPrice), marketPrice, "The market price must be finite and positive.");
            MarketPrice = marketPrice;
        }

        /// <summary>The model price as a function of the model.</summary>
        public Func<HullWhite, double> ModelPrice { get; }

        /// <summary>The price to match.</summary>
        public double MarketPrice { get; }

        /// <summary>
        /// A European swaption quoted by its Black (lognormal) volatility: the market price is
        /// <c>annuity * Black76(S, K, vol sqrt(expiry))</c> with the forward swap rate <c>S</c> and the annuity
        /// <c>sum_i accruals[i] P(0, T_i)</c> of <paramref name="curve"/>.
        /// </summary>
        public static HullWhiteCalibrationTarget Swaption(HullWhite curve, SwaptionType type, double expiry, IReadOnlyList<double> paymentTimes,
            IReadOnlyList<double> accruals, double strike, double blackVolatility)
        {
            ArgumentNullException.ThrowIfNull(curve);
            BlackScholes.RequireNonNegative(blackVolatility, nameof(blackVolatility));
            double forward = curve.ForwardSwapRate(expiry, paymentTimes, accruals);
            double annuity = 0.0;
            for (int i = 0; i < paymentTimes.Count; i++) annuity += accruals[i] * curve.DiscountFactor(paymentTimes[i]);
            double price = annuity * Black76Kernels.NormalisedPrice((int)type, forward, strike, blackVolatility * Math.Sqrt(expiry));
            var times = Copy(paymentTimes);
            var tau = Copy(accruals);
            return new HullWhiteCalibrationTarget(m => m.Swaption(type, expiry, times, tau, strike), price);
        }

        /// <summary>
        /// A cap quoted by one flat Black volatility for all its caplets (caplet <c>i</c>: <c>accrual P(0, end) Black76(L_i, K,
        /// vol sqrt(start))</c>, a caplet fixing at 0 being worth its intrinsic value).
        /// </summary>
        public static HullWhiteCalibrationTarget Cap(HullWhite curve, IReadOnlyList<double> starts, IReadOnlyList<double> ends,
            IReadOnlyList<double> accruals, double strike, double blackVolatility)
        {
            ArgumentNullException.ThrowIfNull(curve);
            BlackScholes.RequireNonNegative(blackVolatility, nameof(blackVolatility));
            double price = 0.0;
            for (int i = 0; i < starts.Count; i++)
            {
                double ps = curve.DiscountFactor(starts[i]), pe = curve.DiscountFactor(ends[i]);
                double forward = (ps / pe - 1.0) / accruals[i];
                price += accruals[i] * pe * Black76Kernels.NormalisedPrice(1, forward, strike, blackVolatility * Math.Sqrt(starts[i]));
            }
            var s = Copy(starts);
            var e = Copy(ends);
            var tau = Copy(accruals);
            return new HullWhiteCalibrationTarget(m => m.Cap(s, e, tau, strike), price);
        }

        private static double[] Copy(IReadOnlyList<double> values)
        {
            ArgumentNullException.ThrowIfNull(values);
            var result = new double[values.Count];
            for (int i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }
    }

    /// <summary>Outcome of <see cref="HullWhiteCalibrator.Calibrate"/>.</summary>
    public sealed class HullWhiteCalibrationResult
    {
        internal HullWhiteCalibrationResult(HullWhite model, double[] relativeErrors, LevenbergMarquardtResult fit)
        {
            Model = model;
            RelativeErrors = relativeErrors;
            Fit = fit;
            double sum = 0.0;
            foreach (var e in relativeErrors) sum += e * e;
            RootMeanSquareRelativeError = Math.Sqrt(sum / relativeErrors.Length);
        }

        /// <summary>The calibrated model, on the curve of the initial one.</summary>
        public HullWhite Model { get; }

        /// <summary><c>model / market - 1</c> per target.</summary>
        public IReadOnlyList<double> RelativeErrors { get; }

        /// <summary>Root mean square of <see cref="RelativeErrors"/>.</summary>
        public double RootMeanSquareRelativeError { get; }

        /// <summary>The optimiser's report.</summary>
        public LevenbergMarquardtResult Fit { get; }
    }

    /// <summary>
    /// Least-squares calibration of the Hull-White parameters to option prices, minimising the relative price errors
    /// <c>model / market - 1</c> with <see cref="LevenbergMarquardt"/> inside the box <c>a</c> in [1e-4, 3],
    /// <c>sigma</c> in [1e-6, 1]. The curve is kept; with <c>calibrateMeanReversion = false</c> only <c>sigma</c> moves.
    /// </summary>
    public static class HullWhiteCalibrator
    {
        /// <summary>Calibrates from the parameters of <paramref name="initial"/>.</summary>
        public static HullWhiteCalibrationResult Calibrate(HullWhite initial, IReadOnlyList<HullWhiteCalibrationTarget> targets,
            bool calibrateMeanReversion = true, LevenbergMarquardtOptions options = null)
        {
            ArgumentNullException.ThrowIfNull(initial);
            ArgumentNullException.ThrowIfNull(targets);
            int n = calibrateMeanReversion ? 2 : 1;
            if (targets.Count < n)
                throw new ArgumentException($"{targets.Count} targets for {n} parameters.", nameof(targets));
            for (int i = 0; i < targets.Count; i++)
                if (targets[i] == null) throw new ArgumentException($"Target {i} is null.", nameof(targets));

            HullWhite ModelOf(double[] p) => calibrateMeanReversion
                ? initial.WithParameters(p[0], p[1])
                : initial.WithParameters(initial.MeanReversion, p[0]);

            void Residuals(double[] p, double[] r)
            {
                var model = ModelOf(p);
                for (int i = 0; i < targets.Count; i++)
                    r[i] = targets[i].ModelPrice(model) / targets[i].MarketPrice - 1.0;
            }

            double sigma0 = Math.Min(Math.Max(initial.Volatility, 1e-4), 0.5);
            double[] guess = calibrateMeanReversion
                ? new[] { Math.Min(Math.Max(initial.MeanReversion, 1e-3), 2.0), sigma0 }
                : new[] { sigma0 };
            options ??= new LevenbergMarquardtOptions
            {
                LowerBounds = calibrateMeanReversion ? new[] { 1e-4, 1e-6 } : new[] { 1e-6 },
                UpperBounds = calibrateMeanReversion ? new[] { 3.0, 1.0 } : new[] { 1.0 },
                FiniteDifference = FiniteDifferenceScheme.Central,
            };
            var fit = LevenbergMarquardt.Minimize(Residuals, guess, targets.Count, null, options);
            var errors = new double[targets.Count];
            Residuals(fit.Parameters, errors);
            return new HullWhiteCalibrationResult(ModelOf(fit.Parameters), errors, fit);
        }
    }
}
