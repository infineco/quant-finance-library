namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Call or put. The underlying value is the payoff sign used by the kernel cores
    /// (<c>sign</c> parameters): +1 for a call, -1 for a put, so that the payoff is
    /// <c>max(sign * (S - K), 0)</c>.
    /// </summary>
    public enum OptionType
    {
        /// <summary>Right to buy: payoff max(S - K, 0).</summary>
        Call = 1,

        /// <summary>Right to sell: payoff max(K - S, 0).</summary>
        Put = -1,
    }
}
