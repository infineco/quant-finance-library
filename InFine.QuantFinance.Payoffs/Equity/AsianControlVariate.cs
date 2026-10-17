using System;

namespace InFine.QuantFinance.Payoffs.Equity
{
    /// <summary>
    /// Outcome of a control-variate Monte Carlo estimate (see <see cref="AsianControlVariate.Estimate(double[], double[], double)"/>).
    /// </summary>
    public sealed class ControlVariateEstimate
    {
        /// <summary>Control-variate estimator mean(X) - beta (mean(Y) - E[Y]).</summary>
        public double Value { get; set; }

        /// <summary>Standard error of <see cref="Value"/> (sample standard deviation of X - beta Y over sqrt(n)).</summary>
        public double StandardError { get; set; }

        /// <summary>Optimal coefficient Cov(X, Y) / Var(Y) estimated on the same sample.</summary>
        public double Beta { get; set; }

        /// <summary>Plain Monte Carlo mean of X.</summary>
        public double PlainValue { get; set; }

        /// <summary>Standard error of the plain mean.</summary>
        public double PlainStandardError { get; set; }

        /// <summary>Sample correlation of X and Y.</summary>
        public double Correlation { get; set; }

        /// <summary>Variance ratio Var(X) / Var(X - beta Y) = 1 / (1 - correlation^2): the estimator needs this many times fewer paths.</summary>
        public double VarianceReduction { get; set; }
    }

    /// <summary>
    /// Host-side control-variate estimator for an Asian option: the arithmetic-average payoff X is paired with the
    /// geometric-average payoff Y of the same paths, whose expectation E[Y] is known in closed form
    /// (<c>AsianKernels.GeometricPrice*</c> in InFine.QuantFinance.ClosedForm, discounted exactly as the kernel does).
    /// The two payoffs are almost perfectly correlated (0.99+ for usual parameters), so the variance falls by
    /// 1 / (1 - rho^2) when beta is chosen optimally.
    /// </summary>
    /// <remarks>
    /// beta is estimated on the same sample as the mean, which introduces a bias of order 1/n (negligible next to the
    /// standard error, and absent when beta comes from a pilot run, hence the <see cref="Estimate(double[], double[], double, double)"/> overload).
    /// The standard error treats beta as fixed, the usual practice.
    /// </remarks>
    public static class AsianControlVariate
    {
        /// <summary>Optimal coefficient beta = Cov(X, Y) / Var(Y) of the sample (0 when Y is constant).</summary>
        public static double OptimalBeta(double[] target, double[] control)
        {
            CheckSamples(target, control);
            int n = target.Length;
            double meanX = Mean(target), meanY = Mean(control);
            double sxy = 0.0, syy = 0.0;
            for (int i = 0; i < n; ++i)
            {
                double dy = control[i] - meanY;
                sxy += (target[i] - meanX) * dy;
                syy += dy * dy;
            }
            return syy > 0.0 ? sxy / syy : 0.0;
        }

        /// <summary>
        /// Control-variate estimate with the sample-optimal beta.
        /// </summary>
        /// <param name="target">Per-path premium of the arithmetic option X (discounted payoffs).</param>
        /// <param name="control">Per-path premium of the geometric option Y on the same paths.</param>
        /// <param name="controlExpectation">E[Y], the closed-form price of the geometric option.</param>
        public static ControlVariateEstimate Estimate(double[] target, double[] control, double controlExpectation)
        {
            return Estimate(target, control, controlExpectation, OptimalBeta(target, control));
        }

        /// <summary>Control-variate estimate with a given beta (for instance from a pilot run).</summary>
        public static ControlVariateEstimate Estimate(double[] target, double[] control, double controlExpectation, double beta)
        {
            CheckSamples(target, control);
            int n = target.Length;
            double meanX = Mean(target), meanY = Mean(control);
            double sxx = 0.0, syy = 0.0, sxy = 0.0, sAdjusted = 0.0;
            for (int i = 0; i < n; ++i)
            {
                double dx = target[i] - meanX, dy = control[i] - meanY;
                sxx += dx * dx;
                syy += dy * dy;
                sxy += dx * dy;
                double da = dx - beta * dy; // adjusted sample X - beta Y, centred
                sAdjusted += da * da;
            }
            double varAdjusted = sAdjusted / (n - 1);
            double varX = sxx / (n - 1);
            double correlation = (sxx > 0.0 && syy > 0.0) ? sxy / Math.Sqrt(sxx * syy) : 0.0;
            return new ControlVariateEstimate
            {
                Value = meanX - beta * (meanY - controlExpectation),
                StandardError = Math.Sqrt(varAdjusted / n),
                Beta = beta,
                PlainValue = meanX,
                PlainStandardError = Math.Sqrt(varX / n),
                Correlation = correlation,
                VarianceReduction = varAdjusted > 0.0 ? varX / varAdjusted : double.PositiveInfinity,
            };
        }

        static double Mean(double[] values)
        {
            double s = 0.0;
            for (int i = 0; i < values.Length; ++i)
                s += values[i];
            return s / values.Length;
        }

        static void CheckSamples(double[] target, double[] control)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (control == null) throw new ArgumentNullException(nameof(control));
            if (target.Length != control.Length) throw new ArgumentException("Samples must have the same length.");
            if (target.Length < 2) throw new ArgumentException("At least two paths are required.");
        }
    }
}
