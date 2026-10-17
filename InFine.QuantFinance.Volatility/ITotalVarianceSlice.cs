namespace InFine.QuantFinance.Volatility
{
    /// <summary>
    /// One expiry of an implied-volatility surface: the total implied variance <c>w(k) = sigma_imp(k)^2 T</c> as a
    /// function of the log-forward moneyness <c>k = ln(K / F_T)</c>, with its first two derivatives in <c>k</c>
    /// (what the butterfly-arbitrage density of <see cref="ArbitrageChecks"/> needs).
    /// </summary>
    public interface ITotalVarianceSlice
    {
        /// <summary>Total implied variance <c>w(k)</c>.</summary>
        double TotalVariance(double k);

        /// <summary><c>dw/dk</c>.</summary>
        double TotalVarianceDerivative(double k);

        /// <summary><c>d^2 w / dk^2</c>.</summary>
        double TotalVarianceSecondDerivative(double k);
    }
}
