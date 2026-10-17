using System;

namespace InFine.QuantFinance.Risk
{
    /// <summary>Outcome of a likelihood-ratio backtest.</summary>
    /// <param name="Statistic">The likelihood-ratio statistic (asymptotically chi-squared under the null).</param>
    /// <param name="PValue">Probability of a statistic at least this large under the null (chi-squared survival).</param>
    /// <param name="DegreesOfFreedom">Degrees of freedom of the reference chi-squared law.</param>
    public readonly record struct LikelihoodRatioTest(double Statistic, double PValue, int DegreesOfFreedom)
    {
        /// <summary>True when the null (a correct VaR model) is rejected at the given significance level, for example 0.05.</summary>
        public bool Rejects(double significance) => PValue < significance;
    }

    /// <summary>
    /// VaR backtesting: exception counting and the likelihood-ratio tests of Kupiec (proportion of failures) and
    /// Christoffersen (independence, conditional coverage). Host-side code.
    /// </summary>
    /// <remarks>
    /// An <b>exception</b> (breach) occurs on a day when the realised loss strictly exceeds that day's VaR, i.e.
    /// <c>-pnl &gt; VaR</c> with the library's loss-positive VaR. For a correct <c>alpha</c> VaR the exceptions are
    /// independent Bernoulli variables with probability <c>p = 1 - alpha</c>. The tests are asymptotic and have little
    /// power on short samples (a 99% VaR needs years of data to be rejected from too few exceptions).
    /// </remarks>
    public static class Backtesting
    {
        /// <summary>Exception indicators: element <c>t</c> is true when <c>-pnl[t] &gt; var[t]</c>.</summary>
        /// <param name="pnl">Realised P&amp;L per day (gain positive).</param>
        /// <param name="var">The VaR forecast for each day (positive loss), same length.</param>
        public static bool[] Exceptions(double[] pnl, double[] var)
        {
            RiskArgs.Finite(pnl, nameof(pnl));
            RiskArgs.Finite(var, nameof(var));
            if (var.Length != pnl.Length) throw new ArgumentException("One VaR forecast per P&L observation is required.", nameof(var));
            var result = new bool[pnl.Length];
            for (int i = 0; i < result.Length; i++) result[i] = -pnl[i] > var[i];
            return result;
        }

        /// <summary>Exception indicators against a constant VaR.</summary>
        public static bool[] Exceptions(double[] pnl, double var)
        {
            RiskArgs.Finite(pnl, nameof(pnl));
            if (!double.IsFinite(var)) throw new ArgumentException("VaR must be finite.", nameof(var));
            var result = new bool[pnl.Length];
            for (int i = 0; i < result.Length; i++) result[i] = -pnl[i] > var;
            return result;
        }

        /// <summary>Number of exceptions (see <see cref="Exceptions(double[], double[])"/>).</summary>
        public static int CountExceptions(double[] pnl, double[] var) => Count(Exceptions(pnl, var));

        /// <summary>Number of exceptions against a constant VaR.</summary>
        public static int CountExceptions(double[] pnl, double var) => Count(Exceptions(pnl, var));

        /// <summary>
        /// Kupiec's proportion-of-failures test (Kupiec, "Techniques for verifying the accuracy of risk measurement
        /// models", Journal of Derivatives 3(2), 1995). Null: the exception probability is <c>p = 1 - confidence</c>. With
        /// <paramref name="exceptions"/> = <c>x</c> out of <paramref name="observations"/> = <c>n</c>,
        /// <c>LR = -2 ln[(1-p)^(n-x) p^x] + 2 ln[(1-x/n)^(n-x) (x/n)^x]</c>, asymptotically chi-squared with one degree
        /// of freedom; <c>0 ln 0 = 0</c> covers the boundary counts. It rejects both too many exceptions (an
        /// understated VaR) and too few (an overly conservative one).
        /// </summary>
        public static LikelihoodRatioTest KupiecPof(int observations, int exceptions, double confidence)
        {
            RiskArgs.Confidence(confidence);
            if (observations < 1) throw new ArgumentOutOfRangeException(nameof(observations));
            if (exceptions < 0 || exceptions > observations) throw new ArgumentOutOfRangeException(nameof(exceptions));
            double p = 1.0 - confidence;
            int n = observations, x = exceptions;
            double rate = x / (double)n;
            double lr = -2.0 * (Bernoulli(n - x, x, 1.0 - p, p) - Bernoulli(n - x, x, 1.0 - rate, rate));
            lr = Math.Max(lr, 0.0); // the unrestricted likelihood is the maximum; clip rounding noise
            return new LikelihoodRatioTest(lr, ChiSquared.Survival1(lr), 1);
        }

        /// <summary>Kupiec test from exception indicators.</summary>
        public static LikelihoodRatioTest KupiecPof(bool[] exceptions, double confidence)
        {
            ArgumentNullException.ThrowIfNull(exceptions);
            return KupiecPof(exceptions.Length, Count(exceptions), confidence);
        }

        /// <summary>
        /// Christoffersen's independence test (Christoffersen, "Evaluating interval forecasts", International Economic
        /// Review 39(4), 1998): are exceptions serially independent? Counts the transitions <c>n_ab</c> (state a on a day,
        /// b on the next, 1 = exception) of the indicator series; the null is a first-order Markov chain with equal
        /// exception probability whether or not the previous day was an exception. With
        /// <c>pi01 = n01/(n00+n01)</c>, <c>pi11 = n11/(n10+n11)</c>, <c>pi = (n01+n11)/(n00+n01+n10+n11)</c>:
        /// <c>LR = -2 ln[(1-pi)^(n00+n10) pi^(n01+n11)] + 2 ln[(1-pi01)^n00 pi01^n01 (1-pi11)^n10 pi11^n11]</c>,
        /// chi-squared with one degree of freedom. It detects clustering of exceptions, not their number. Needs at least
        /// two observations; a series with no exception at all (or no non-exception) carries no information and gives
        /// statistic 0, p-value 1.
        /// </summary>
        public static LikelihoodRatioTest ChristoffersenIndependence(bool[] exceptions)
        {
            ArgumentNullException.ThrowIfNull(exceptions);
            if (exceptions.Length < 2) throw new ArgumentException("At least two observations are required.", nameof(exceptions));
            int n00 = 0, n01 = 0, n10 = 0, n11 = 0;
            for (int t = 1; t < exceptions.Length; t++)
            {
                if (exceptions[t - 1]) { if (exceptions[t]) n11++; else n10++; }
                else { if (exceptions[t]) n01++; else n00++; }
            }
            double lr = IndependenceStatistic(n00, n01, n10, n11);
            return new LikelihoodRatioTest(lr, ChiSquared.Survival1(lr), 1);
        }

        /// <summary>
        /// Christoffersen's conditional-coverage test: the sum of the Kupiec statistic (correct exception rate) and the
        /// independence statistic, chi-squared with two degrees of freedom (the p-value is <c>exp(-LR/2)</c>).
        /// </summary>
        public static LikelihoodRatioTest ChristoffersenConditionalCoverage(bool[] exceptions, double confidence)
        {
            LikelihoodRatioTest pof = KupiecPof(exceptions, confidence);
            LikelihoodRatioTest ind = ChristoffersenIndependence(exceptions);
            double lr = pof.Statistic + ind.Statistic;
            return new LikelihoodRatioTest(lr, ChiSquared.Survival2(lr), 2);
        }

        // ---- internals -----------------------------------------------------------------------------------------

        static int Count(bool[] flags)
        {
            int c = 0;
            foreach (bool b in flags) if (b) c++;
            return c;
        }

        /// <summary>ln[(1-q)^a q^b] with <c>0 ln 0 = 0</c>; arguments are the two exponents and probabilities (1-q, q).</summary>
        static double Bernoulli(int zeros, int ones, double probZero, double probOne) =>
            Term(zeros, probZero) + Term(ones, probOne);

        static double Term(int count, double prob) => count == 0 ? 0.0 : count * Math.Log(prob);

        static double IndependenceStatistic(int n00, int n01, int n10, int n11)
        {
            int fromZero = n00 + n01, fromOne = n10 + n11, total = fromZero + fromOne;
            double pi = (n01 + n11) / (double)total;
            double logNull = Term(n00 + n10, 1.0 - pi) + Term(n01 + n11, pi);
            double pi01 = fromZero == 0 ? 0.0 : n01 / (double)fromZero;
            double pi11 = fromOne == 0 ? 0.0 : n11 / (double)fromOne;
            double logAlt = Term(n00, 1.0 - pi01) + Term(n01, pi01) + Term(n10, 1.0 - pi11) + Term(n11, pi11);
            return Math.Max(-2.0 * (logNull - logAlt), 0.0);
        }
    }
}
