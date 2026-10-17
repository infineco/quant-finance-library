using System;

namespace InFine.QuantFinance.ClosedForm
{
    /// <summary>
    /// Heston stochastic-volatility parameters: <c>dv = Kappa (Theta - v) dt + Xi sqrt(v) dW2</c>, <c>v(0) = V0</c>,
    /// correlation <c>Rho</c> between the spot and variance Brownian motions.
    /// </summary>
    /// <param name="V0">initial variance, non-negative.</param>
    /// <param name="Kappa">mean-reversion speed, positive.</param>
    /// <param name="Theta">long-run variance, non-negative.</param>
    /// <param name="Xi">volatility of variance, non-negative (0 gives deterministic variance).</param>
    /// <param name="Rho">spot-variance correlation, in [-1, 1].</param>
    public readonly record struct HestonParameters(double V0, double Kappa, double Theta, double Xi, double Rho)
    {
        /// <summary>
        /// Feller ratio <c>2 Kappa Theta / Xi^2</c>; the variance stays strictly positive when it is at least 1.
        /// </summary>
        public double FellerRatio => 2.0 * Kappa * Theta / (Xi * Xi);

        /// <summary>True when <c>2 Kappa Theta &gt;= Xi^2</c>.</summary>
        public bool SatisfiesFeller => 2.0 * Kappa * Theta >= Xi * Xi;

        /// <summary>Throws <see cref="ArgumentOutOfRangeException"/> unless the parameters are admissible.</summary>
        public void Validate()
        {
            BlackScholes.RequireNonNegative(V0, nameof(V0));
            BlackScholes.RequireNonNegative(Theta, nameof(Theta));
            BlackScholes.RequireNonNegative(Xi, nameof(Xi));
            if (!(Kappa > 0.0) || double.IsInfinity(Kappa))
                throw new ArgumentOutOfRangeException(nameof(Kappa), Kappa, "Kappa must be finite and positive.");
            if (!(Math.Abs(Rho) <= 1.0))
                throw new ArgumentOutOfRangeException(nameof(Rho), Rho, "Rho must lie in [-1, 1].");
        }
    }

    /// <summary>
    /// Host-friendly API over <see cref="HestonKernels"/>: semi-analytic Heston prices of European options on a
    /// forward by the COS method. Arguments are validated (exceptions); the kernels never throw.
    /// </summary>
    public static class Heston
    {
        /// <summary>
        /// European option price under Heston.
        /// </summary>
        /// <param name="type">call or put.</param>
        /// <param name="forward">forward of the underlying for the expiry, positive.</param>
        /// <param name="strike">strike, positive.</param>
        /// <param name="discountFactor">discount factor to the payment date, positive.</param>
        /// <param name="maturity">time to expiry in years, positive.</param>
        /// <param name="parameters">Heston parameters.</param>
        /// <param name="terms">number of cosine terms N (see <see cref="HestonKernels"/> for the accuracy).</param>
        /// <param name="truncationWidth">truncation-range half width L in standard deviations of ln(S_T / F).</param>
        public static double Price(OptionType type, double forward, double strike, double discountFactor,
            double maturity, HestonParameters parameters, int terms = HestonKernels.DefaultTerms,
            double truncationWidth = HestonKernels.DefaultTruncationWidth)
        {
            Validate(type, forward, strike, discountFactor, maturity);
            ValidateModel(parameters, terms, truncationWidth);
            return HestonKernels.Price((int)type, forward, strike, discountFactor, maturity,
                parameters.V0, parameters.Kappa, parameters.Theta, parameters.Xi, parameters.Rho, terms, truncationWidth);
        }

        /// <summary>
        /// Batch of European prices under one parameter set, element by element (see the scalar overload). All arrays
        /// must have the same length; every element is validated before any is priced.
        /// </summary>
        public static double[] Price(OptionType[] type, double[] forward, double[] strike, double[] discountFactor,
            double[] maturity, HestonParameters parameters, int terms = HestonKernels.DefaultTerms,
            double truncationWidth = HestonKernels.DefaultTruncationWidth)
        {
            ArgumentNullException.ThrowIfNull(type);
            ArgumentNullException.ThrowIfNull(forward);
            ArgumentNullException.ThrowIfNull(strike);
            ArgumentNullException.ThrowIfNull(discountFactor);
            ArgumentNullException.ThrowIfNull(maturity);
            int n = type.Length;
            if (forward.Length != n || strike.Length != n || discountFactor.Length != n || maturity.Length != n)
                throw new ArgumentException("All input arrays must have the same length.");
            ValidateModel(parameters, terms, truncationWidth);

            var sign = new int[n];
            for (int i = 0; i < n; i++)
            {
                Validate(type[i], forward[i], strike[i], discountFactor[i], maturity[i]);
                sign[i] = (int)type[i];
            }
            var price = new double[n];
            for (int i = 0; i < n; i++)
                HestonKernels.PriceAt(i, price, sign, forward, strike, discountFactor, maturity,
                    parameters.V0, parameters.Kappa, parameters.Theta, parameters.Xi, parameters.Rho, terms, truncationWidth);
            return price;
        }

        /// <summary>
        /// Characteristic function <c>E[exp(i u ln(S_T / F))]</c> of the log-forward at the expiry, for real u.
        /// </summary>
        public static System.Numerics.Complex CharacteristicFunction(double u, double maturity, HestonParameters parameters)
        {
            BlackScholes.RequireFinite(u, nameof(u));
            RequirePositive(maturity, nameof(maturity));
            parameters.Validate();
            HestonKernels.CharacteristicFunction(u, maturity, parameters.V0, parameters.Kappa, parameters.Theta,
                parameters.Xi, parameters.Rho, out double re, out double im);
            return new System.Numerics.Complex(re, im);
        }

        static void Validate(OptionType type, double forward, double strike, double discountFactor, double maturity)
        {
            if (type != OptionType.Call && type != OptionType.Put)
                throw new ArgumentOutOfRangeException(nameof(type), type, "Option type must be Call or Put.");
            RequirePositive(forward, nameof(forward));
            RequirePositive(strike, nameof(strike));
            RequirePositive(discountFactor, nameof(discountFactor));
            RequirePositive(maturity, nameof(maturity));
        }

        static void ValidateModel(HestonParameters parameters, int terms, double truncationWidth)
        {
            parameters.Validate();
            if (terms < 1)
                throw new ArgumentOutOfRangeException(nameof(terms), terms, "At least one cosine term is required.");
            RequirePositive(truncationWidth, nameof(truncationWidth));
        }

        static void RequirePositive(double value, string name)
        {
            if (!(value > 0.0) || double.IsInfinity(value))
                throw new ArgumentOutOfRangeException(name, value, name + " must be finite and positive.");
        }
    }
}
