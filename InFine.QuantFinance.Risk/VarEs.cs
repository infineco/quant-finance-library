using System;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// A Value-at-Risk / Expected Shortfall pair. Sign convention for the whole library: profit-and-loss (P&amp;L) is
    /// positive for a gain; <b>VaR and ES are reported as losses</b>, i.e. a positive number is a loss of that size.
    /// They are not clamped: a negative value means that even the tail of the distribution is a gain.
    /// </summary>
    /// <param name="Var">Value-at-Risk at the confidence level: the loss exceeded with probability <c>1 - confidence</c>.</param>
    /// <param name="ExpectedShortfall">Expected loss in the worst <c>1 - confidence</c> of outcomes (always at least <see cref="Var"/> for the Lower quantile and the parametric measures).</param>
    public readonly record struct VarEs(double Var, double ExpectedShortfall);

    /// <summary>How the historical VaR picks the quantile of the empirical loss distribution.</summary>
    public enum QuantileMethod
    {
        /// <summary>
        /// The left-continuous empirical quantile <c>inf{x : F(x) &gt;= alpha}</c>, i.e. the loss <c>L_(k)</c> with
        /// <c>k = ceil(n alpha)</c> in the ascending order statistics. A realised loss, never interpolated; this is the
        /// quantile for which the Acerbi-Tasche expected shortfall is the exact tail average. Default.
        /// </summary>
        Lower,

        /// <summary>
        /// Linear interpolation between order statistics at the (zero-based) position <c>(n - 1) alpha</c> (Hyndman and
        /// Fan type 7, the default of R and NumPy). Equal-weight samples only.
        /// </summary>
        Linear,
    }

    /// <summary>
    /// Euler decomposition of VaR and ES over the positions of a portfolio. All amounts follow the loss-positive
    /// convention of <see cref="VarEs"/>. Arrays are indexed by position.
    /// </summary>
    /// <remarks>
    /// Marginal risk is the derivative with respect to a position size (<c>d risk / d w_i</c>); component risk is
    /// <c>w_i</c> times it, and the components add up to the total (Euler's theorem for degree-one homogeneous
    /// measures) - exactly for the parametric measures and for historical ES, and by construction for historical VaR
    /// (see <see cref="HistoricalRisk.Decompose"/>). Incremental risk is the total minus the total with the position
    /// removed; unlike the components it does not add up.
    /// </remarks>
    public sealed class RiskDecomposition
    {
        internal RiskDecomposition(VarEs total, double[] marginalVar, double[] componentVar, double[] marginalEs, double[] componentEs,
            double[] incrementalVar, double[] incrementalEs)
        {
            Total = total;
            MarginalVar = marginalVar;
            ComponentVar = componentVar;
            MarginalEs = marginalEs;
            ComponentEs = componentEs;
            IncrementalVar = incrementalVar;
            IncrementalEs = incrementalEs;
        }

        /// <summary>Portfolio VaR and ES.</summary>
        public VarEs Total { get; }

        /// <summary>Marginal VaR <c>d VaR / d w_i</c>: extra VaR per unit of extra position.</summary>
        public double[] MarginalVar { get; }

        /// <summary>Component VaR <c>w_i d VaR / d w_i</c>; sums to <c>Total.Var</c>.</summary>
        public double[] ComponentVar { get; }

        /// <summary>Marginal ES <c>d ES / d w_i</c>.</summary>
        public double[] MarginalEs { get; }

        /// <summary>Component ES <c>w_i d ES / d w_i</c>; sums to <c>Total.ExpectedShortfall</c>.</summary>
        public double[] ComponentEs { get; }

        /// <summary>Incremental VaR: <c>VaR(all) - VaR(without position i)</c>.</summary>
        public double[] IncrementalVar { get; }

        /// <summary>Incremental ES: <c>ES(all) - ES(without position i)</c>.</summary>
        public double[] IncrementalEs { get; }

        /// <summary>Component VaR as a fraction of the total VaR (sums to 1). NaN entries if the total is zero.</summary>
        public double[] PercentVar => Percent(ComponentVar, Total.Var);

        /// <summary>Component ES as a fraction of the total ES (sums to 1). NaN entries if the total is zero.</summary>
        public double[] PercentEs => Percent(ComponentEs, Total.ExpectedShortfall);

        static double[] Percent(double[] component, double total)
        {
            var result = new double[component.Length];
            for (int i = 0; i < result.Length; i++) result[i] = total == 0.0 ? double.NaN : component[i] / total;
            return result;
        }
    }

    /// <summary>Argument validation shared by the VaR/ES code.</summary>
    internal static class RiskArgs
    {
        internal static void Confidence(double confidence)
        {
            if (!(confidence > 0.0 && confidence < 1.0))
                throw new ArgumentOutOfRangeException(nameof(confidence), "Confidence must be strictly between 0 and 1 (for example 0.99).");
        }

        internal static void Finite(double[] values, string name, int minLength = 1)
        {
            ArgumentNullException.ThrowIfNull(values, name);
            if (values.Length < minLength) throw new ArgumentException($"At least {minLength} value(s) required.", name);
            foreach (double v in values)
                if (!double.IsFinite(v)) throw new ArgumentException("Values must be finite.", name);
        }

        /// <summary>Validates probability weights (non-negative, finite, positive sum) and returns them normalised to sum 1.</summary>
        internal static double[] NormalisedWeights(double[] weights, int expectedLength, string name)
        {
            ArgumentNullException.ThrowIfNull(weights, name);
            if (weights.Length != expectedLength) throw new ArgumentException($"Expected {expectedLength} weights, got {weights.Length}.", name);
            double sum = 0.0;
            foreach (double w in weights)
            {
                if (!(w >= 0.0) || double.IsInfinity(w)) throw new ArgumentException("Weights must be finite and non-negative.", name);
                sum += w;
            }
            if (!(sum > 0.0) || double.IsInfinity(sum)) throw new ArgumentException("Weights must have a positive finite sum.", name);
            var result = new double[weights.Length];
            for (int i = 0; i < result.Length; i++) result[i] = weights[i] / sum;
            return result;
        }
    }
}
