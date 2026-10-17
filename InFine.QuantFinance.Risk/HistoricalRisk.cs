using System;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Historical-simulation Value-at-Risk and Expected Shortfall, with age-weighted and volatility-scaled variants and
    /// an Euler decomposition over positions. Host-side code.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Sign convention.</b> P&amp;L is positive for a gain; VaR and ES are returned as <b>positive losses</b>
    /// (see <see cref="VarEs"/>). A loss sample is <c>L_i = -pnl_i</c>, with ascending order statistics
    /// <c>L_(1) &lt;= ... &lt;= L_(n)</c>; <c>alpha</c> is the confidence level (0.99) and <c>1 - alpha</c> the tail mass.
    /// </para>
    /// <para>
    /// <b>Quantile.</b> Each scenario carries a probability weight <c>w_i</c> (1/n unless weights are given). The
    /// empirical distribution function is <c>F(x) = sum of w_i over L_i &lt;= x</c> and, by default
    /// (<see cref="QuantileMethod.Lower"/>), <c>VaR = inf{x : F(x) &gt;= alpha}</c>: the loss at which the cumulative
    /// weight first reaches alpha. With equal weights that is <c>L_(ceil(n alpha))</c>. The
    /// <see cref="QuantileMethod.Linear"/> alternative interpolates linearly between order statistics (equal weights only).
    /// </para>
    /// <para>
    /// <b>Expected Shortfall</b> follows Acerbi and Tasche ("On the coherence of expected shortfall", Journal of Banking
    /// and Finance 26, 2002): the scenarios are sorted by loss and the worst probability mass <c>1 - alpha</c> is
    /// averaged, <c>ES = (1/(1-alpha)) * integral_alpha^1 q_u du</c>, where the scenario that straddles the boundary
    /// <c>alpha</c> enters with only the fraction of its weight that lies inside the tail. So ES is the exact tail
    /// average for any sample size (for n = 20 and alpha = 0.95 it is the worst loss; for alpha = 0.9 it averages the
    /// two worst) and is a coherent (sub-additive) measure of the empirical distribution, whereas "the mean of the
    /// losses beyond VaR" is not. ES is never below the Lower VaR.
    /// </para>
    /// <para>
    /// Historical measures describe one holding period: use returns over the horizon you want to measure (overlapping
    /// multi-day returns if needed). Nothing is scaled by the square root of time here.
    /// </para>
    /// </remarks>
    public static class HistoricalRisk
    {
        // A cumulative weight within this distance of alpha counts as having reached it (rounding of summed weights).
        const double CumulativeTolerance = 1e-12;

        // ---- P&L vectors ---------------------------------------------------------------------------------------

        /// <summary>Historical VaR and ES of a P&amp;L sample with equal scenario weights.</summary>
        /// <param name="pnl">Portfolio P&amp;L per scenario (gain positive), finite, at least one value.</param>
        /// <param name="confidence">Confidence level in (0, 1), for example 0.99.</param>
        /// <param name="method">Quantile definition for VaR; ES is always the Acerbi-Tasche tail average.</param>
        public static VarEs Measures(double[] pnl, double confidence, QuantileMethod method = QuantileMethod.Lower)
        {
            RiskArgs.Finite(pnl, nameof(pnl));
            RiskArgs.Confidence(confidence);
            return Profile(Losses(pnl), null, confidence, method).Measures;
        }

        /// <summary>Historical VaR (positive loss) of a P&amp;L sample; see <see cref="Measures(double[], double, QuantileMethod)"/>.</summary>
        public static double ValueAtRisk(double[] pnl, double confidence, QuantileMethod method = QuantileMethod.Lower) =>
            Measures(pnl, confidence, method).Var;

        /// <summary>Historical Expected Shortfall (positive loss) of a P&amp;L sample, Acerbi-Tasche tail average.</summary>
        public static double ExpectedShortfall(double[] pnl, double confidence) =>
            Measures(pnl, confidence).ExpectedShortfall;

        /// <summary>
        /// VaR and ES of a P&amp;L sample whose scenarios carry unequal probability weights (Lower quantile).
        /// Weights are non-negative and are normalised to sum 1.
        /// </summary>
        public static VarEs WeightedMeasures(double[] pnl, double[] weights, double confidence)
        {
            RiskArgs.Finite(pnl, nameof(pnl));
            RiskArgs.Confidence(confidence);
            double[] w = RiskArgs.NormalisedWeights(weights, pnl.Length, nameof(weights));
            return Profile(Losses(pnl), w, confidence, QuantileMethod.Lower).Measures;
        }

        // ---- scenarios x positions -----------------------------------------------------------------------------

        /// <summary>
        /// Portfolio P&amp;L per scenario, <c>pnl_t = sum_j positions_j * scenarioReturns[t, j]</c>.
        /// </summary>
        /// <param name="scenarioReturns">T x N returns (rows = scenarios, columns = assets), finite.</param>
        /// <param name="positions">Position (currency exposure) per asset, length N, finite; negative is short.</param>
        public static double[] PortfolioPnl(double[,] scenarioReturns, double[] positions)
        {
            RequireScenarios(scenarioReturns, positions);
            int t = scenarioReturns.GetLength(0), n = scenarioReturns.GetLength(1);
            var pnl = new double[t];
            for (int i = 0; i < t; i++)
            {
                double s = 0.0;
                for (int j = 0; j < n; j++) s += positions[j] * scenarioReturns[i, j];
                pnl[i] = s;
            }
            return pnl;
        }

        /// <summary>Historical VaR and ES of the portfolio with the given positions over equally weighted return scenarios.</summary>
        public static VarEs Measures(double[,] scenarioReturns, double[] positions, double confidence, QuantileMethod method = QuantileMethod.Lower) =>
            Measures(PortfolioPnl(scenarioReturns, positions), confidence, method);

        // ---- age weighting (Boudoukh-Richardson-Whitelaw) ------------------------------------------------------

        /// <summary>
        /// Boudoukh-Richardson-Whitelaw age weights (Boudoukh, Richardson, Whitelaw, "The best of both worlds", Risk 11,
        /// 1998): for <paramref name="count"/> observations ordered oldest first, observation <c>i</c> (0 = oldest) gets
        /// <c>w_i = lambda^(n-1-i) (1 - lambda) / (1 - lambda^n)</c>, so the most recent has the largest weight and the
        /// weights sum to 1. <c>lambda = 1</c> gives equal weights. Typical lambda: 0.97 to 0.99.
        /// </summary>
        public static double[] AgeWeights(int count, double lambda)
        {
            if (count < 1) throw new ArgumentOutOfRangeException(nameof(count));
            if (!(lambda > 0.0 && lambda <= 1.0)) throw new ArgumentOutOfRangeException(nameof(lambda), "Lambda must be in (0, 1].");
            var w = new double[count];
            double sum = 0.0, p = 1.0;
            for (int i = count - 1; i >= 0; i--)
            {
                w[i] = p;
                sum += p;
                p *= lambda;
            }
            for (int i = 0; i < count; i++) w[i] /= sum; // sum = (1 - lambda^n)/(1 - lambda); normalising is the BRW formula, robust near lambda = 1
            return w;
        }

        /// <summary>
        /// Age-weighted (BRW) VaR and ES of a P&amp;L history ordered oldest first, with
        /// <see cref="AgeWeights"/>(n, <paramref name="lambda"/>).
        /// </summary>
        public static VarEs AgeWeightedMeasures(double[] pnl, double lambda, double confidence)
        {
            RiskArgs.Finite(pnl, nameof(pnl));
            return WeightedMeasures(pnl, AgeWeights(pnl.Length, lambda), confidence);
        }

        // ---- volatility scaling (Hull-White filtered historical simulation) ------------------------------------

        /// <summary>
        /// Volatility-scaled scenario returns of Hull and White ("Incorporating volatility updating into the historical
        /// simulation method for value-at-risk", Journal of Risk 1(1), 1998). For each asset the EWMA variance forecast
        /// <c>s2_1 = (1/k) sum_{i&lt;=k} r_i^2</c> (k = <paramref name="seedWindow"/>) and
        /// <c>s2_{t+1} = lambda s2_t + (1 - lambda) r_t^2</c> gives <c>sigma_t</c>, the volatility forecast for day t
        /// made before seeing <c>r_t</c>, and <c>sigma_{T+1}</c>, the forecast for the next day. Each historical return
        /// is rescaled to today's volatility, <c>r*_t = r_t sigma_{T+1} / sigma_t</c>, so quiet-period scenarios no
        /// longer understate risk in a turbulent market (and vice versa).
        /// </summary>
        /// <param name="returns">T x N returns (rows = dates oldest first), T at least 2.</param>
        /// <param name="lambda">EWMA decay in (0, 1); 0.94 is the RiskMetrics daily value.</param>
        /// <param name="seedWindow">Number of leading rows (clipped to T) whose mean square seeds the variance; at least 1. The seed
        /// must be non-zero for every asset.</param>
        public static double[,] VolatilityScaledReturns(double[,] returns, double lambda = EwmaCovariance.DefaultLambda, int seedWindow = 20)
        {
            Covariance.RequireReturns(returns, 2);
            if (!(lambda > 0.0 && lambda < 1.0)) throw new ArgumentOutOfRangeException(nameof(lambda), "Lambda must be in (0, 1).");
            if (seedWindow < 1) throw new ArgumentOutOfRangeException(nameof(seedWindow));
            int t = returns.GetLength(0), n = returns.GetLength(1);
            int k = Math.Min(seedWindow, t);
            var scaled = new double[t, n];
            var variance = new double[t + 1];
            for (int j = 0; j < n; j++)
            {
                double seed = 0.0;
                for (int i = 0; i < k; i++) seed += returns[i, j] * returns[i, j];
                seed /= k;
                if (!(seed > 0.0)) throw new ArgumentException($"Asset {j} has zero variance over the seed window.", nameof(returns));
                variance[0] = seed;
                for (int i = 0; i < t; i++) variance[i + 1] = lambda * variance[i] + (1.0 - lambda) * returns[i, j] * returns[i, j];
                for (int i = 0; i < t; i++) scaled[i, j] = returns[i, j] * Math.Sqrt(variance[t] / variance[i]);
            }
            return scaled;
        }

        /// <summary>
        /// Filtered historical simulation: VaR and ES of the portfolio over <see cref="VolatilityScaledReturns"/> scenarios,
        /// optionally also age-weighted.
        /// </summary>
        /// <param name="returns">T x N raw returns, oldest first.</param>
        /// <param name="positions">Position per asset.</param>
        /// <param name="confidence">Confidence level.</param>
        /// <param name="lambda">EWMA decay for the volatility filter.</param>
        /// <param name="seedWindow">Variance seed window.</param>
        /// <param name="method">Quantile definition (Lower only if <paramref name="ageLambda"/> is given).</param>
        /// <param name="ageLambda">If set, additionally weights scenarios by BRW age weights with this lambda.</param>
        public static VarEs VolatilityScaledMeasures(double[,] returns, double[] positions, double confidence,
            double lambda = EwmaCovariance.DefaultLambda, int seedWindow = 20, QuantileMethod method = QuantileMethod.Lower, double? ageLambda = null)
        {
            double[] pnl = PortfolioPnl(VolatilityScaledReturns(returns, lambda, seedWindow), positions);
            return ageLambda.HasValue ? AgeWeightedMeasures(pnl, ageLambda.Value, confidence) : Measures(pnl, confidence, method);
        }

        // ---- decomposition -------------------------------------------------------------------------------------

        /// <summary>
        /// Euler decomposition of the historical VaR and ES of a portfolio over its return scenarios, plus incremental
        /// risk (recomputed without each position).
        /// </summary>
        /// <param name="scenarioReturns">T x N returns, finite.</param>
        /// <param name="positions">Position per asset, length N.</param>
        /// <param name="confidence">Confidence level.</param>
        /// <param name="scenarioWeights">Optional scenario probability weights (for example <see cref="AgeWeights"/>); null is equal weights.</param>
        /// <param name="method">VaR quantile definition (Lower only with weights).</param>
        /// <param name="bandwidth">Kernel bandwidth, in loss units, for component VaR; null uses Silverman's rule
        /// <c>1.06 s n_eff^(-1/5)</c> with <c>s</c> the loss standard deviation.</param>
        /// <returns>The decomposition; components sum to the totals (see remarks).</returns>
        /// <remarks>
        /// <para>
        /// <b>Component ES</b> is exact. With the Acerbi-Tasche tail weights <c>a_t</c> (sum 1) of the scenarios sorted by
        /// portfolio loss, <c>ES = sum_t a_t L_t</c> and <c>L_t = sum_j w_j (-r_tj)</c>, so
        /// <c>marginal ES_j = sum_t a_t (-r_tj)</c> (the average loss of asset j in the portfolio's tail: the
        /// conditional tail expectation) and <c>component ES_j = w_j marginal ES_j</c>; the components add up to ES to
        /// rounding error. Scenarios tied in loss are ordered by index, which decides who carries a boundary weight.
        /// </para>
        /// <para>
        /// <b>Component VaR</b> is <c>E[-w_j r_j | L = VaR]</c> (Tasche 2002; Gourieroux, Laurent and Scaillet 2000),
        /// a conditional expectation given a zero-probability event, which a finite sample cannot evaluate exactly. It
        /// is estimated by a Nadaraya-Watson Gaussian kernel on the portfolio loss, centred at VaR: scenarios whose loss is
        /// near VaR are averaged with weights <c>exp(-((L_t - VaR)/h)^2/2)</c>. Kernel smoothing biases the sum of the
        /// estimates slightly, so they are rescaled by one common factor so that the components add up to VaR exactly;
        /// the factor is close to 1 unless the bandwidth is too wide. Estimates are noisy: the effective number of
        /// scenarios near VaR is roughly <c>n h f(VaR)</c>, a handful for a 99% VaR on a few hundred scenarios - widen
        /// the bandwidth or use a larger scenario set. A single-scenario estimate (bandwidth tending to 0) is exact in
        /// total but extremely noisy. The allocation requires <c>VaR &gt; 0</c> (otherwise <see cref="InvalidOperationException"/>).
        /// </para>
        /// </remarks>
        public static RiskDecomposition Decompose(double[,] scenarioReturns, double[] positions, double confidence,
            double[]? scenarioWeights = null, QuantileMethod method = QuantileMethod.Lower, double? bandwidth = null)
        {
            RequireScenarios(scenarioReturns, positions);
            RiskArgs.Confidence(confidence);
            if (bandwidth.HasValue && !(bandwidth.Value > 0.0 && double.IsFinite(bandwidth.Value)))
                throw new ArgumentOutOfRangeException(nameof(bandwidth), "Bandwidth must be positive and finite.");
            int t = scenarioReturns.GetLength(0), n = scenarioReturns.GetLength(1);
            double[]? w = scenarioWeights == null ? null : RiskArgs.NormalisedWeights(scenarioWeights, t, nameof(scenarioWeights));
            if (w != null && method == QuantileMethod.Linear)
                throw new ArgumentException("The Linear quantile is defined for equal scenario weights only.", nameof(method));

            double[] loss = Losses(PortfolioPnl(scenarioReturns, positions));
            TailProfile profile = Profile(loss, w, confidence, method);
            VarEs total = profile.Measures;

            // Component ES: exact Euler allocation over the tail weights.
            var marginalEs = new double[n];
            var componentEs = new double[n];
            for (int i = 0; i < t; i++)
            {
                double a = profile.TailWeight[i];
                if (a == 0.0) continue;
                for (int j = 0; j < n; j++) marginalEs[j] -= a * scenarioReturns[i, j];
            }
            for (int j = 0; j < n; j++) componentEs[j] = positions[j] * marginalEs[j];

            // Component VaR: kernel conditional expectation given L = VaR, rescaled to add up to VaR.
            if (!(total.Var > 0.0))
                throw new InvalidOperationException("Component VaR needs a positive VaR (a loss at the chosen confidence level).");
            double[] k = KernelWeights(loss, w, total.Var, bandwidth);
            var marginalVar = new double[n];
            double ksum = 0.0;
            for (int i = 0; i < t; i++)
            {
                double ki = k[i];
                if (ki == 0.0) continue;
                ksum += ki;
                for (int j = 0; j < n; j++) marginalVar[j] -= ki * scenarioReturns[i, j];
            }
            double implied = 0.0;
            for (int j = 0; j < n; j++)
            {
                marginalVar[j] /= ksum;
                implied += positions[j] * marginalVar[j];
            }
            if (!(implied > 0.0))
                throw new InvalidOperationException("The kernel neighbourhood of VaR has no net loss; widen the bandwidth.");
            double scale = total.Var / implied;
            var componentVar = new double[n];
            for (int j = 0; j < n; j++)
            {
                marginalVar[j] *= scale;
                componentVar[j] = positions[j] * marginalVar[j];
            }

            // Incremental risk: recompute with the position removed.
            var incVar = new double[n];
            var incEs = new double[n];
            var reduced = new double[t];
            for (int j = 0; j < n; j++)
            {
                for (int i = 0; i < t; i++) reduced[i] = loss[i] + positions[j] * scenarioReturns[i, j];
                VarEs without = Profile(reduced, w, confidence, method).Measures;
                incVar[j] = total.Var - without.Var;
                incEs[j] = total.ExpectedShortfall - without.ExpectedShortfall;
            }

            return new RiskDecomposition(total, marginalVar, componentVar, marginalEs, componentEs, incVar, incEs);
        }

        // ---- internals -----------------------------------------------------------------------------------------

        /// <summary>Sorted-loss analysis of a weighted sample: VaR, ES and the per-scenario tail weights (sum 1) with ES = sum a_t L_t.</summary>
        sealed class TailProfile
        {
            public VarEs Measures;
            public double[] TailWeight = Array.Empty<double>();
        }

        static TailProfile Profile(double[] losses, double[]? weights, double alpha, QuantileMethod method)
        {
            int n = losses.Length;
            if (weights != null && method == QuantileMethod.Linear)
                throw new ArgumentException("The Linear quantile is defined for equal scenario weights only.", nameof(method));
            var order = new int[n];
            for (int i = 0; i < n; i++) order[i] = i;
            Array.Sort(order, (a, b) =>
            {
                int c = losses[a].CompareTo(losses[b]);
                return c != 0 ? c : a.CompareTo(b);
            });

            var tail = new double[n];
            double prev = 0.0, cum = 0.0, overlapSum = 0.0;
            int varPos = -1;
            for (int j = 0; j < n; j++)
            {
                int idx = order[j];
                double hi;
                if (weights == null) hi = (j + 1) / (double)n;
                else
                {
                    cum += weights[idx];
                    hi = j == n - 1 ? 1.0 : Math.Min(1.0, cum);
                }
                if (varPos < 0 && hi >= alpha - CumulativeTolerance) varPos = j;
                double overlap = hi - Math.Max(prev, alpha);
                if (overlap > 0.0)
                {
                    tail[idx] = overlap;
                    overlapSum += overlap;
                }
                prev = hi;
            }
            if (varPos < 0) varPos = n - 1; // unreachable for weights summing to 1; guards rounding

            double var;
            if (method == QuantileMethod.Lower) var = losses[order[varPos]];
            else
            {
                double h = (n - 1) * alpha;
                int lo = (int)Math.Floor(h);
                double frac = h - lo;
                var = lo + 1 < n ? losses[order[lo]] + frac * (losses[order[lo + 1]] - losses[order[lo]]) : losses[order[lo]];
            }

            double es = 0.0;
            for (int i = 0; i < n; i++)
            {
                tail[i] /= overlapSum;
                es += tail[i] * losses[i];
            }
            return new TailProfile { Measures = new VarEs(var, es), TailWeight = tail };
        }

        /// <summary>Gaussian kernel weights (scenario weight x kernel) around <paramref name="centre"/>, rescaled so the largest term is 1 (no underflow).</summary>
        static double[] KernelWeights(double[] loss, double[]? weights, double centre, double? bandwidth)
        {
            int t = loss.Length;
            var k = new double[t];
            double h;
            if (bandwidth.HasValue) h = bandwidth.Value;
            else
            {
                double mean = 0.0, sumSq = 0.0;
                for (int i = 0; i < t; i++) mean += (weights == null ? 1.0 / t : weights[i]) * loss[i];
                double w2 = 0.0;
                for (int i = 0; i < t; i++)
                {
                    double wi = weights == null ? 1.0 / t : weights[i];
                    sumSq += wi * (loss[i] - mean) * (loss[i] - mean);
                    w2 += wi * wi;
                }
                h = 1.06 * Math.Sqrt(sumSq) * Math.Pow(1.0 / w2, -0.2); // Silverman, effective sample size 1 / sum(w^2)
            }
            if (!(h > 0.0))
            {
                for (int i = 0; i < t; i++) k[i] = weights == null ? 1.0 : weights[i];
                return k;
            }
            double emin = double.PositiveInfinity;
            var e = new double[t];
            for (int i = 0; i < t; i++)
            {
                double z = (loss[i] - centre) / h;
                e[i] = 0.5 * z * z;
                if ((weights == null || weights[i] > 0.0) && e[i] < emin) emin = e[i];
            }
            for (int i = 0; i < t; i++)
                k[i] = (weights == null ? 1.0 : weights[i]) * Math.Exp(emin - e[i]);
            return k;
        }

        static double[] Losses(double[] pnl)
        {
            var loss = new double[pnl.Length];
            for (int i = 0; i < loss.Length; i++) loss[i] = -pnl[i];
            return loss;
        }

        static void RequireScenarios(double[,] scenarioReturns, double[] positions)
        {
            Covariance.RequireReturns(scenarioReturns, 1);
            RiskArgs.Finite(positions, nameof(positions));
            if (positions.Length != scenarioReturns.GetLength(1))
                throw new ArgumentException($"Expected one position per asset ({scenarioReturns.GetLength(1)}).", nameof(positions));
        }
    }
}
