namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Price and analytic sensitivities of a European option under Black-Scholes-Merton or Black-76.
    /// All derivatives are raw partial derivatives of the price (no scaling per percent or per day).
    /// </summary>
    /// <remarks>
    /// Conventions, with V the price, T the time to expiry in years and t the calendar time (t = -T):
    /// <list type="bullet">
    /// <item><description><see cref="Theta"/> is dV/dt = -dV/dT, per year, other inputs held fixed:
    /// it is negative for a long option that loses time value. Divide by 365 for a per-day figure.</description></item>
    /// <item><description><see cref="Rho"/> is dV/dr and <see cref="DividendRho"/> is dV/dq (BSM). For Black-76 the
    /// forward and discount factor are inputs: Rho is dV/dr at fixed forward (= -T V) and DividendRho is 0.</description></item>
    /// <item><description><see cref="Delta"/>, <see cref="Gamma"/> and <see cref="Vanna"/> are derivatives with respect to the
    /// spot S (BSM) or the forward F (Black-76).</description></item>
    /// <item><description><see cref="Vega"/> is dV/d(sigma), <see cref="Vanna"/> is d2V/(dS d sigma) and
    /// <see cref="Volga"/> (vomma) is d2V/d(sigma)^2.</description></item>
    /// <item><description><see cref="DualDelta"/> is dV/dK.</description></item>
    /// </list>
    /// </remarks>
    public readonly struct OptionGreeks
    {
        public double Price { get; }
        public double Delta { get; }
        public double Gamma { get; }
        public double Vega { get; }
        public double Theta { get; }
        public double Rho { get; }
        public double DividendRho { get; }
        public double Vanna { get; }
        public double Volga { get; }
        public double DualDelta { get; }

        public OptionGreeks(double price, double delta, double gamma, double vega, double theta,
            double rho, double dividendRho, double vanna, double volga, double dualDelta)
        {
            Price = price;
            Delta = delta;
            Gamma = gamma;
            Vega = vega;
            Theta = theta;
            Rho = rho;
            DividendRho = dividendRho;
            Vanna = vanna;
            Volga = volga;
            DualDelta = dualDelta;
        }
    }
}
