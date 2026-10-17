using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Host-friendly API over <see cref="BlackScholesKernels"/>: generalised Black-Scholes-Merton with a continuous
    /// dividend yield. Arguments are validated (exceptions); the kernels themselves never throw.
    /// </summary>
    public static class BlackScholes
    {
        /// <summary>
        /// European option price.
        /// </summary>
        /// <param name="type">call or put.</param>
        /// <param name="spot">value of the underlying, non-negative.</param>
        /// <param name="strike">strike, non-negative.</param>
        /// <param name="maturity">time to expiry in years, non-negative. 0 gives the intrinsic value.</param>
        /// <param name="rate">continuously compounded risk-free rate.</param>
        /// <param name="dividendYield">continuous dividend yield.</param>
        /// <param name="volatility">lognormal volatility, non-negative. 0 gives the discounted intrinsic value of the forward.</param>
        public static double Price(OptionType type, double spot, double strike, double maturity,
            double rate, double dividendYield, double volatility)
        {
            Validate(type, spot, "spot", strike, maturity, rate, dividendYield, volatility);
            return BlackScholesKernels.Price((int)type, spot, strike, maturity, rate, dividendYield, volatility);
        }

        /// <summary>
        /// European option price and analytic Greeks; conventions are documented on <see cref="OptionGreeks"/>.
        /// </summary>
        public static OptionGreeks Greeks(OptionType type, double spot, double strike, double maturity,
            double rate, double dividendYield, double volatility)
        {
            Validate(type, spot, "spot", strike, maturity, rate, dividendYield, volatility);
            BlackScholesKernels.Greeks((int)type, spot, strike, maturity, rate, dividendYield, volatility,
                out double price, out double delta, out double gamma, out double vega, out double theta,
                out double rho, out double dividendRho, out double vanna, out double volga, out double dualDelta);
            return new OptionGreeks(price, delta, gamma, vega, theta, rho, dividendRho, vanna, volga, dualDelta);
        }

        internal static void Validate(OptionType type, double underlying, string underlyingName, double strike,
            double maturity, double rate, double dividendYield, double volatility)
        {
            if (type != OptionType.Call && type != OptionType.Put)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Option type must be Call or Put.");
            RequireNonNegative(underlying, underlyingName);
            RequireNonNegative(strike, nameof(strike));
            RequireNonNegative(maturity, nameof(maturity));
            RequireNonNegative(volatility, nameof(volatility));
            RequireFinite(rate, nameof(rate));
            RequireFinite(dividendYield, nameof(dividendYield));
        }

        internal static void RequireNonNegative(double value, string name)
        {
            if (!(value >= 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite and non-negative.");
        }

        internal static void RequireFinite(double value, string name)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite.");
        }
    }

    /// <summary>
    /// Host-friendly API over <see cref="Black76Kernels"/>: European options on a forward.
    /// </summary>
    public static class Black76
    {
        /// <summary>
        /// Black-76 price: discountFactor * undiscounted Black price.
        /// </summary>
        /// <param name="type">call or put.</param>
        /// <param name="forward">forward for the expiry, non-negative.</param>
        /// <param name="strike">strike, non-negative.</param>
        /// <param name="discountFactor">discount factor to the payment date, non-negative.</param>
        /// <param name="maturity">time to expiry in years, non-negative.</param>
        /// <param name="volatility">lognormal volatility of the forward, non-negative.</param>
        public static double Price(OptionType type, double forward, double strike, double discountFactor,
            double maturity, double volatility)
        {
            BlackScholes.Validate(type, forward, nameof(forward), strike, maturity, 0.0, 0.0, volatility);
            BlackScholes.RequireNonNegative(discountFactor, nameof(discountFactor));
            return Black76Kernels.Price((int)type, forward, strike, discountFactor, maturity, volatility);
        }

        /// <summary>
        /// Undiscounted Black price from the total standard deviation v = sigma sqrt(T): the normalised form used
        /// by implied-volatility solvers.
        /// </summary>
        public static double NormalisedPrice(OptionType type, double forward, double strike, double stdDev)
        {
            BlackScholes.Validate(type, forward, nameof(forward), strike, 0.0, 0.0, 0.0, stdDev);
            return Black76Kernels.NormalisedPrice((int)type, forward, strike, stdDev);
        }

        /// <summary>
        /// Black-76 price and Greeks with discount factor exp(-rate * maturity). Delta and gamma are derivatives with
        /// respect to the forward, theta and rho hold the forward fixed, dividend rho is 0 (see <see cref="OptionGreeks"/>).
        /// </summary>
        public static OptionGreeks Greeks(OptionType type, double forward, double strike, double rate,
            double maturity, double volatility)
        {
            BlackScholes.Validate(type, forward, nameof(forward), strike, maturity, rate, 0.0, volatility);
            Black76Kernels.Greeks((int)type, forward, strike, rate, maturity, volatility,
                out double price, out double delta, out double gamma, out double vega, out double theta,
                out double rho, out double vanna, out double volga, out double dualDelta);
            return new OptionGreeks(price, delta, gamma, vega, theta, rho, 0.0, vanna, volga, dualDelta);
        }
    }
}
