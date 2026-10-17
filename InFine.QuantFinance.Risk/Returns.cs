using System;

namespace InFine.QuantFinance.Risk
{
    /// <summary>
    /// Returns from prices. Price matrices are T x N with rows = dates (oldest first) and columns = assets; the
    /// result has T - 1 rows. Prices must be finite and strictly positive.
    /// </summary>
    public static class Returns
    {
        /// <summary>Simple returns <c>P_t / P_{t-1} - 1</c>.</summary>
        public static double[,] Simple(double[,] prices) => Compute(prices, simple: true);

        /// <summary>Log returns <c>ln(P_t / P_{t-1})</c>.</summary>
        public static double[,] Log(double[,] prices) => Compute(prices, simple: false);

        /// <summary>Simple returns of one price series.</summary>
        public static double[] Simple(double[] prices) => Compute(prices, simple: true);

        /// <summary>Log returns of one price series.</summary>
        public static double[] Log(double[] prices) => Compute(prices, simple: false);

        static double[,] Compute(double[,] prices, bool simple)
        {
            ArgumentNullException.ThrowIfNull(prices);
            int t = prices.GetLength(0), n = prices.GetLength(1);
            if (t < 2) throw new ArgumentException("At least two price dates are needed.", nameof(prices));
            var result = new double[t - 1, n];
            for (int j = 0; j < n; j++)
            {
                RequirePrice(prices[0, j]);
                for (int i = 1; i < t; i++)
                {
                    RequirePrice(prices[i, j]);
                    double ratio = prices[i, j] / prices[i - 1, j];
                    result[i - 1, j] = simple ? ratio - 1.0 : Math.Log(ratio);
                }
            }
            return result;
        }

        static double[] Compute(double[] prices, bool simple)
        {
            ArgumentNullException.ThrowIfNull(prices);
            if (prices.Length < 2) throw new ArgumentException("At least two prices are needed.", nameof(prices));
            RequirePrice(prices[0]);
            var result = new double[prices.Length - 1];
            for (int i = 1; i < prices.Length; i++)
            {
                RequirePrice(prices[i]);
                double ratio = prices[i] / prices[i - 1];
                result[i - 1] = simple ? ratio - 1.0 : Math.Log(ratio);
            }
            return result;
        }

        static void RequirePrice(double p)
        {
            if (!(p > 0.0) || double.IsInfinity(p))
                throw new ArgumentException("Prices must be finite and strictly positive.");
        }
    }
}
