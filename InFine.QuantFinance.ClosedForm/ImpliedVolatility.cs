using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Host-friendly API over <see cref="ImpliedVolatilityKernels"/> (Jäckel's "Let's Be Rational"): Black-76 and
    /// Black-Scholes-Merton implied volatilities to machine precision wherever the price determines them.
    /// </summary>
    /// <remarks>
    /// Market inputs (forward or spot, strike, maturity, discount factor or rates, option type) are validated and
    /// invalid ones throw. The price only has to be finite and non-negative: a price outside the arbitrage bounds is
    /// a market-data fact, not a programming error, so it is reported in the result as in the kernels: NaN below
    /// the (discounted) intrinsic value, 0 exactly at it, +Infinity at or above the upper bound (the discounted
    /// forward for a call, the discounted strike for a put).
    /// </remarks>
    public static class ImpliedVolatility
    {
        /// <summary>
        /// Black-76 implied volatility of a European option on a forward.
        /// </summary>
        /// <param name="forward">forward of the underlying for the expiry, positive.</param>
        /// <param name="strike">strike, positive.</param>
        /// <param name="maturity">time to expiry in years, positive.</param>
        /// <param name="discountFactor">discount factor from the payment date to the valuation date, positive.</param>
        /// <param name="price">discounted option price, finite and non-negative.</param>
        /// <param name="type">call or put.</param>
        public static double Black76(double forward, double strike, double maturity, double discountFactor,
            double price, OptionType type)
        {
            ValidateBlack76(forward, strike, maturity, discountFactor, price, type);
            return ImpliedVolatilityKernels.Black76((int)type, price, forward, strike, discountFactor, maturity);
        }

        /// <summary>
        /// Implied total standard deviation <c>sigma sqrt(T)</c> of a Black-76 price, for callers that work in total
        /// volatility (no maturity needed).
        /// </summary>
        public static double Black76StandardDeviation(double forward, double strike, double discountFactor,
            double price, OptionType type)
        {
            ValidateBlack76(forward, strike, 1.0, discountFactor, price, type);
            return ImpliedVolatilityKernels.StandardDeviation((int)type, price, forward, strike, discountFactor);
        }

        /// <summary>
        /// Generalised Black-Scholes-Merton implied volatility (continuous dividend yield q): the Black-76 implied
        /// volatility on the forward <c>S exp((r - q) T)</c> with discount factor <c>exp(-r T)</c>.
        /// </summary>
        /// <param name="spot">value of the underlying, positive.</param>
        /// <param name="strike">strike, positive.</param>
        /// <param name="maturity">time to expiry in years, positive.</param>
        /// <param name="rate">continuously compounded risk-free rate.</param>
        /// <param name="dividendYield">continuous dividend yield.</param>
        /// <param name="price">option price, finite and non-negative.</param>
        /// <param name="type">call or put.</param>
        public static double BlackScholes(double spot, double strike, double maturity, double rate,
            double dividendYield, double price, OptionType type)
        {
            ClosedForm.BlackScholes.RequireFinite(rate, nameof(rate));
            ClosedForm.BlackScholes.RequireFinite(dividendYield, nameof(dividendYield));
            ValidateBlack76(spot, strike, maturity, 1.0, price, type, nameof(spot));
            double forward = spot * Math.Exp((rate - dividendYield) * maturity);
            double discountFactor = Math.Exp(-rate * maturity);
            RequirePositive(forward, "forward");
            RequirePositive(discountFactor, "discount factor");
            return ImpliedVolatilityKernels.Black76((int)type, price, forward, strike, discountFactor, maturity);
        }

        /// <summary>
        /// Batch of Black-76 implied volatilities, element by element (see the scalar overload). All arrays must have
        /// the same length; every element is validated before any is solved.
        /// </summary>
        public static double[] Black76(double[] forward, double[] strike, double[] maturity, double[] discountFactor,
            double[] price, OptionType[] type)
        {
            ArgumentNullException.ThrowIfNull(forward);
            ArgumentNullException.ThrowIfNull(strike);
            ArgumentNullException.ThrowIfNull(maturity);
            ArgumentNullException.ThrowIfNull(discountFactor);
            ArgumentNullException.ThrowIfNull(price);
            ArgumentNullException.ThrowIfNull(type);
            int n = forward.Length;
            if (strike.Length != n || maturity.Length != n || discountFactor.Length != n || price.Length != n || type.Length != n)
                throw new ArgumentException("All input arrays must have the same length.");

            var sign = new int[n];
            for (int i = 0; i < n; i++)
            {
                ValidateBlack76(forward[i], strike[i], maturity[i], discountFactor[i], price[i], type[i]);
                sign[i] = (int)type[i];
            }
            var volatility = new double[n];
            for (int i = 0; i < n; i++)
                ImpliedVolatilityKernels.Black76At(i, volatility, sign, price, forward, strike, discountFactor, maturity);
            return volatility;
        }

        static void ValidateBlack76(double forward, double strike, double maturity, double discountFactor,
            double price, OptionType type, string forwardName = "forward")
        {
            if (type != OptionType.Call && type != OptionType.Put)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Option type must be Call or Put.");
            RequirePositive(forward, forwardName);
            RequirePositive(strike, nameof(strike));
            RequirePositive(maturity, nameof(maturity));
            RequirePositive(discountFactor, nameof(discountFactor));
            if (!(price >= 0.0) || double.IsInfinity(price))
                throw new ArgumentOutOfRangeException(nameof(price), price, "price must be finite and non-negative.");
        }

        static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite and positive.");
        }
    }
}
