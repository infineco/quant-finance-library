using System;

namespace InFine.QuantFinance.Portfolio
{
    /// <summary>
    /// Volatility risk decomposition of a portfolio, with the same conventions as the delta-normal decomposition of
    /// <c>InFine.QuantFinance.Risk.ParametricRisk.Decompose</c>: for weights <c>w</c> and covariance <c>Sigma</c>,
    /// <c>sigma_p = sqrt(w' Sigma w)</c>, marginal risk <c>(Sigma w)_i / sigma_p</c> (the gradient of volatility),
    /// component (absolute) risk <c>w_i x marginal_i</c> which sums to <c>sigma_p</c> (Euler), and relative
    /// contribution <c>component_i / sigma_p</c> which sums to one. Dividing a Risk component VaR by the total VaR gives
    /// the same relative contributions as <see cref="Relative"/>.
    /// </summary>
    public sealed class RiskContributions
    {
        internal RiskContributions(double volatility, double[] marginal, double[] absolute, double[] relative)
        {
            Volatility = volatility;
            Marginal = marginal;
            Absolute = absolute;
            Relative = relative;
        }

        /// <summary>Portfolio volatility <c>sqrt(w' Sigma w)</c>, in the units of the covariance.</summary>
        public double Volatility { get; }

        /// <summary>Marginal risk contribution <c>d sigma_p / d w_i = (Sigma w)_i / sigma_p</c>.</summary>
        public double[] Marginal { get; }

        /// <summary>Absolute (component) risk contribution <c>w_i (Sigma w)_i / sigma_p</c>; sums to <see cref="Volatility"/>.</summary>
        public double[] Absolute { get; }

        /// <summary>Relative (percentage) risk contribution <c>w_i (Sigma w)_i / sigma_p^2</c>; sums to 1.</summary>
        public double[] Relative { get; }
    }
}
