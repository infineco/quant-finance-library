using System;

namespace InFine.QuantFinance.TimeSeries
{
    /// <summary>Distribution of the standardised innovations <c>z_t</c> of a GARCH model (zero mean, unit variance).</summary>
    public enum InnovationDistribution
    {
        /// <summary>Standard normal.</summary>
        Normal,

        /// <summary>Student t with <c>nu &gt; 2</c> degrees of freedom, rescaled to unit variance: <c>z = t_nu sqrt((nu - 2) / nu)</c>.</summary>
        StudentT,
    }

    /// <summary>How the conditional variance recursion is started, i.e. the value used for <c>sigma^2_1</c>.</summary>
    public enum VarianceInitialisation
    {
        /// <summary><c>sigma^2_1 = mean(eps_t^2)</c> over the whole sample (<c>eps_t = r_t - mu</c>). Default; the common textbook choice.</summary>
        SampleVariance,

        /// <summary>
        /// Exponentially weighted backcast of the first observations, as in Sheppard's <c>arch</c> package:
        /// <c>sigma^2_1 = sum_i w_i eps_i^2</c> over the first <c>min(75, T)</c> observations with <c>w_i</c> proportional to
        /// <c>0.94^i</c>. Depends only on the start of the sample, so it matters less for long series and less for strong persistence than it might seem.
        /// </summary>
        Backcast,
    }

    /// <summary>
    /// Parameters of a constant-mean GARCH(1,1): <c>r_t = mu + eps_t</c>, <c>eps_t = sigma_t z_t</c>,
    /// <c>sigma^2_t = omega + alpha eps^2_{t-1} + beta sigma^2_{t-1}</c>.
    /// </summary>
    /// <param name="Mu">Conditional mean per period.</param>
    /// <param name="Omega">Variance intercept, strictly positive.</param>
    /// <param name="Alpha">Reaction to the last squared innovation, at least 0.</param>
    /// <param name="Beta">Persistence of the variance, at least 0 (<c>Alpha + Beta &lt; 1</c> for covariance stationarity).</param>
    /// <param name="Nu">Student-t degrees of freedom (above 2); positive infinity for normal innovations.</param>
    public readonly record struct GarchParameters(double Mu, double Omega, double Alpha, double Beta, double Nu = double.PositiveInfinity)
    {
        /// <summary><c>alpha + beta</c>: how fast a variance shock decays (the half-life is <c>ln 0.5 / ln(persistence)</c> periods).</summary>
        public double Persistence => Alpha + Beta;

        /// <summary>Unconditional (long-run) variance per period <c>omega / (1 - alpha - beta)</c>.</summary>
        public double UnconditionalVariance => Omega / (1.0 - Persistence);

        /// <summary>Throws <see cref="ArgumentException"/> unless the parameters satisfy the GARCH constraints (and <c>nu &gt; 2</c> for Student-t).</summary>
        public void Validate(InnovationDistribution distribution)
        {
            if (!double.IsFinite(Mu)) throw new ArgumentException("mu must be finite.");
            if (!(Omega > 0.0) || double.IsInfinity(Omega)) throw new ArgumentException("omega must be strictly positive and finite.");
            if (!(Alpha >= 0.0) || double.IsInfinity(Alpha)) throw new ArgumentException("alpha must be non-negative and finite.");
            if (!(Beta >= 0.0) || double.IsInfinity(Beta)) throw new ArgumentException("beta must be non-negative and finite.");
            if (!(Alpha + Beta < 1.0)) throw new ArgumentException("alpha + beta must be below 1 (covariance stationarity).");
            if (distribution == InnovationDistribution.StudentT && !(Nu > 2.0 && !double.IsNaN(Nu)))
                throw new ArgumentException("nu must be above 2 for Student-t innovations.");
        }
    }

    /// <summary>Settings of <see cref="Garch11.Fit"/>.</summary>
    public sealed class GarchOptions
    {
        /// <summary>Innovation distribution. Default normal (Gaussian quasi-maximum likelihood when the data are fat tailed).</summary>
        public InnovationDistribution Distribution { get; init; } = InnovationDistribution.Normal;

        /// <summary>Estimate a constant mean <c>mu</c> jointly (default). When false the mean is fixed at 0 and the returns are the innovations.</summary>
        public bool EstimateMean { get; init; } = true;

        /// <summary>
        /// Variance targeting: fix <c>omega = (1 - alpha - beta) v</c> where <c>v</c> is the sample variance (about the sample
        /// mean, or the mean square when the mean is not estimated) and estimate only <c>alpha</c> and <c>beta</c>. Removes
        /// the poorly identified intercept at the price of tying the long-run variance to the sample.
        /// </summary>
        public bool VarianceTargeting { get; init; }

        /// <summary>How <c>sigma^2_1</c> is chosen. Default <see cref="VarianceInitialisation.SampleVariance"/>.</summary>
        public VarianceInitialisation Initialisation { get; init; } = VarianceInitialisation.SampleVariance;

        /// <summary>Also compute Bollerslev-Wooldridge (1992) robust standard errors (sandwich estimator). Default false.</summary>
        public bool RobustStandardErrors { get; init; }

        /// <summary>Starting values; null tries several (alpha, beta) starts and keeps the best optimum. <c>omega</c> and <c>mu</c> are ignored under targeting / fixed mean.</summary>
        public GarchParameters? StartingValues { get; init; }

        /// <summary>Maximum BFGS iterations per start. Default 500.</summary>
        public int MaxIterations { get; init; } = 500;
    }

    /// <summary>A fitted GARCH(1,1) and its diagnostics, returned by <see cref="Garch11.Fit"/>.</summary>
    public sealed class GarchFit
    {
        internal GarchFit(GarchParameters parameters, GarchParameters standardErrors, GarchParameters? robustStandardErrors,
            double logLikelihood, int count, int estimatedParameters, InnovationDistribution distribution,
            double[] conditionalVariance, double[] standardisedResiduals, double nextVariance, double initialVariance,
            string[] parameterNames, double[,]? covariance, bool converged, int iterations, bool atBoundary, bool hessianPositiveDefinite)
        {
            Parameters = parameters;
            StandardErrors = standardErrors;
            RobustStandardErrors = robustStandardErrors;
            LogLikelihood = logLikelihood;
            ObservationCount = count;
            EstimatedParameterCount = estimatedParameters;
            Distribution = distribution;
            ConditionalVariance = conditionalVariance;
            StandardisedResiduals = standardisedResiduals;
            NextVariance = nextVariance;
            InitialVariance = initialVariance;
            ParameterNames = parameterNames;
            Covariance = covariance;
            Converged = converged;
            Iterations = iterations;
            AtBoundary = atBoundary;
            HessianPositiveDefinite = hessianPositiveDefinite;
        }

        /// <summary>The maximum-likelihood estimates.</summary>
        public GarchParameters Parameters { get; }

        /// <summary>Standard errors from the inverse of minus the numerical Hessian of the log-likelihood; NaN for fixed parameters and when unavailable (see <see cref="HessianPositiveDefinite"/>, <see cref="AtBoundary"/>). Under variance targeting the <c>omega</c> entry is a delta-method value that ignores the estimation error of the target.</summary>
        public GarchParameters StandardErrors { get; }

        /// <summary>Bollerslev-Wooldridge sandwich standard errors, when requested through <see cref="GarchOptions.RobustStandardErrors"/>.</summary>
        public GarchParameters? RobustStandardErrors { get; }

        /// <summary>Maximised log-likelihood (sum over observations).</summary>
        public double LogLikelihood { get; }

        /// <summary>Number of returns <c>T</c>.</summary>
        public int ObservationCount { get; }

        /// <summary>Number of freely estimated parameters (mu, omega, alpha, beta, nu as applicable).</summary>
        public int EstimatedParameterCount { get; }

        /// <summary>Innovation distribution of the fit.</summary>
        public InnovationDistribution Distribution { get; }

        /// <summary>Akaike information criterion <c>2k - 2 logL</c>.</summary>
        public double Aic => 2.0 * EstimatedParameterCount - 2.0 * LogLikelihood;

        /// <summary>Bayesian information criterion <c>k ln T - 2 logL</c>.</summary>
        public double Bic => EstimatedParameterCount * Math.Log(ObservationCount) - 2.0 * LogLikelihood;

        /// <summary>Fitted conditional variances <c>sigma^2_t</c>, one per return.</summary>
        public double[] ConditionalVariance { get; }

        /// <summary>Standardised residuals <c>(r_t - mu) / sigma_t</c>.</summary>
        public double[] StandardisedResiduals { get; }

        /// <summary>One-step-ahead variance <c>sigma^2_{T+1}</c>, the starting point of the forecasts.</summary>
        public double NextVariance { get; }

        /// <summary>The value used for <c>sigma^2_1</c>.</summary>
        public double InitialVariance { get; }

        /// <summary>Names of the freely estimated parameters, in the order of <see cref="Covariance"/>.</summary>
        public string[] ParameterNames { get; }

        /// <summary>Covariance matrix of the freely estimated parameters (inverse of minus the Hessian), or null when unavailable.</summary>
        public double[,]? Covariance { get; }

        /// <summary>True when the optimiser met a convergence criterion.</summary>
        public bool Converged { get; }

        /// <summary>BFGS iterations of the best start.</summary>
        public int Iterations { get; }

        /// <summary>
        /// True when the estimate sits on the boundary of the constraint set (alpha or beta within 1e-6 of 0, or
        /// alpha + beta within 1e-6 of 1): there the likelihood is not locally quadratic and the standard errors are not reported.
        /// </summary>
        public bool AtBoundary { get; }

        /// <summary>False when minus the numerical Hessian was not positive definite, in which case the standard errors are NaN.</summary>
        public bool HessianPositiveDefinite { get; }

        /// <summary>Forecasts of the conditional variance for horizons 1 to <paramref name="horizon"/>; see <see cref="Garch11.ForecastVariance"/>.</summary>
        public double[] ForecastVariance(int horizon) => Garch11.ForecastVariance(Parameters, NextVariance, horizon);

        /// <summary>Annualised volatility term structure for horizons 1 to <paramref name="maxHorizon"/>; see <see cref="Garch11.AnnualisedVolatilityTermStructure"/>.</summary>
        public double[] AnnualisedVolatilityTermStructure(int maxHorizon, double periodsPerYear) =>
            Garch11.AnnualisedVolatilityTermStructure(Parameters, NextVariance, maxHorizon, periodsPerYear);
    }

    /// <summary>A simulated GARCH path.</summary>
    /// <param name="Returns">Simulated returns <c>r_t = mu + sigma_t z_t</c>.</param>
    /// <param name="ConditionalVariance">The conditional variances <c>sigma^2_t</c> that generated them.</param>
    public sealed record GarchSimulation(double[] Returns, double[] ConditionalVariance);
}
