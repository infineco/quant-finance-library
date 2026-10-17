using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Hand-written adjoint (reverse mode) of the Heston full-truncation Euler scheme of <see cref="HestonStep"/>: one
    /// backward sweep per path gives the pathwise derivative of a discounted payoff with respect to the spot, the initial
    /// variance, kappa, theta, xi, rho and the rate. Hybridizer-free (plain arrays with offsets), kernel-compatible.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Step k, spot and variance both driven by the truncated variance <c>v_k^+ = max(v_k, 0)</c> of the start of the step
    /// (the scheme of <see cref="HestonVolatilityModel{Volatility, Rate, Brownian, Schedule}"/> followed by
    /// <see cref="EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/>, up to the rounding of
    /// <c>sqrt(v^+)^2</c>), with <c>z_V = rho z_S + sqrt(1 - rho^2) z_perp</c> (<see cref="HestonStep.CorrelatedDriver"/>):
    /// $$ x_{k+1} = x_k + (r - v_k^+ / 2) dt_k + \sqrt{v_k^+} \sqrt{dt_k} z_S, \qquad v_{k+1} = v_k + \kappa (\theta - v_k^+) dt_k + \xi \sqrt{v_k^+} \sqrt{dt_k} z_V $$
    /// and <c>P = exp(-r T) g(S_0, ..., S_n)</c> with <c>T = sum_k dt_k</c>. Unlike the log-normal step the Jacobian in
    /// the state is not the identity (<c>dv_{k+1}/dv_k</c>, <c>dx_{k+1}/dv_k</c>), so the backward sweep carries both
    /// adjoints and needs the stored variances: this is where reverse mode does real work.
    /// </para>
    /// <para>
    /// Kink of <c>v^+</c>: its derivative is taken as <c>1</c> for <c>v &gt; 0</c> and <c>0</c> for <c>v &lt;= 0</c>,
    /// i.e. at <c>v = 0</c> the variance is treated as locally constant. This is the one-sided convention of the dual
    /// numbers written <c>Dual.Max(0.0, v)</c> (the first argument is kept on a tie), and it is the one that stays
    /// finite: with the other side, <c>sqrt(v^+)</c> would have an infinite derivative at 0. Paths reach <c>v = 0</c>
    /// exactly with probability zero unless <c>v_0 = 0</c>. The pathwise estimator itself is only as good as the
    /// discretised map is Lipschitz: near <c>v = 0</c> the derivative of <c>sqrt(v^+)</c> is unbounded, so with the Feller
    /// condition violated the Greeks of the volatility parameters have heavy-tailed samples.
    /// </para>
    /// <para>
    /// Normals: <c>2 steps</c> per path, <c>z_S</c> of step k at <c>2 k</c> and <c>z_perp</c> at <c>2 k + 1</c>.
    /// Gradient layout: <see cref="SpotIndex"/>, <see cref="InitialVarianceIndex"/>, <see cref="KappaIndex"/>,
    /// <see cref="ThetaIndex"/>, <see cref="XiIndex"/>, <see cref="CorrelationIndex"/>, <see cref="RateIndex"/>.
    /// </para>
    /// </remarks>
    public static class HestonAdjoint
    {
        /// <summary>Gradient index of the initial spot.</summary>
        public const int SpotIndex = 0;

        /// <summary>Gradient index of the initial variance v_0.</summary>
        public const int InitialVarianceIndex = 1;

        /// <summary>Gradient index of the mean-reversion speed kappa.</summary>
        public const int KappaIndex = 2;

        /// <summary>Gradient index of the long-term variance theta.</summary>
        public const int ThetaIndex = 3;

        /// <summary>Gradient index of the volatility of variance xi.</summary>
        public const int XiIndex = 4;

        /// <summary>Gradient index of the spot/variance correlation rho.</summary>
        public const int CorrelationIndex = 5;

        /// <summary>Gradient index of the rate (drift and discounting).</summary>
        public const int RateIndex = 6;

        /// <summary>Number of gradient entries.</summary>
        public const int GradientLength = 7;

        /// <summary>
        /// Forward sweep of one path through <see cref="HestonStep"/>: writes <c>ln S(t_k)</c> and the raw (possibly
        /// negative) variance <c>v_k</c>, k = 0..steps, and returns the discount factor <c>exp(-r T)</c>.
        /// </summary>
        /// <param name="spot">Initial spot S_0 &gt; 0.</param>
        /// <param name="v0">Initial variance.</param>
        /// <param name="kappa">Mean-reversion speed.</param>
        /// <param name="theta">Long-term variance.</param>
        /// <param name="xi">Volatility of variance.</param>
        /// <param name="rho">Spot/variance correlation, |rho| &lt; 1.</param>
        /// <param name="rate">Risk-free rate.</param>
        /// <param name="dt">Step lengths, <paramref name="steps"/> positive entries.</param>
        /// <param name="steps">Number of steps.</param>
        /// <param name="normals">Draws, <c>2 steps</c> entries from <paramref name="normalOffset"/> (see remarks).</param>
        /// <param name="normalOffset">Index of the first draw.</param>
        /// <param name="logPath">Output, <c>steps + 1</c> entries from <paramref name="pathOffset"/>.</param>
        /// <param name="pathOffset">Index of ln S_0.</param>
        /// <param name="variance">Output, <c>steps + 1</c> raw variances from <paramref name="varianceOffset"/>.</param>
        /// <param name="varianceOffset">Index of v_0.</param>
        [Kernel]
        public static double Forward(double spot, double v0, double kappa, double theta, double xi, double rho, double rate,
            double[] dt, int steps, double[] normals, int normalOffset, double[] logPath, int pathOffset,
            double[] variance, int varianceOffset)
        {
            double logSpot = Math.Log(spot);
            double v = v0;
            double maturity = 0.0;
            logPath[pathOffset] = logSpot;
            variance[varianceOffset] = v;
            for (int k = 0; k < steps; ++k)
            {
                double h = dt[k];
                double sqrtH = Math.Sqrt(h);
                double zSpot = normals[normalOffset + 2 * k];
                double zVariance = HestonStep.CorrelatedDriver(zSpot, normals[normalOffset + 2 * k + 1], rho);
                logSpot = HestonStep.LogSpotEuler(logSpot, v, rate, h, sqrtH, zSpot);
                v = HestonStep.FullTruncationEuler(v, kappa, theta, xi, h, sqrtH, zVariance);
                logPath[pathOffset + k + 1] = logSpot;
                variance[varianceOffset + k + 1] = v;
                maturity += h;
            }
            return Math.Exp(-rate * maturity);
        }

        /// <summary>
        /// Backward sweep of one path. On entry <paramref name="logSpotAdjoint"/> holds the direct derivatives
        /// <c>dP/dx_k</c> of the discounted payoff (k = 0..steps); on return the total derivatives <c>dP/d ln S(t_k)</c>.
        /// The derivatives of <paramref name="premium"/> are added to <paramref name="gradient"/> in the layout of
        /// <see cref="HestonAdjoint"/>.
        /// </summary>
        /// <param name="spot">Initial spot, as given to <see cref="Forward"/>.</param>
        /// <param name="kappa">Mean-reversion speed.</param>
        /// <param name="theta">Long-term variance.</param>
        /// <param name="xi">Volatility of variance.</param>
        /// <param name="rho">Spot/variance correlation, |rho| &lt; 1.</param>
        /// <param name="dt">Step lengths.</param>
        /// <param name="steps">Number of steps.</param>
        /// <param name="normals">The draws of the forward sweep.</param>
        /// <param name="normalOffset">Index of the first draw.</param>
        /// <param name="variance">Raw variances written by <see cref="Forward"/>.</param>
        /// <param name="varianceOffset">Index of v_0.</param>
        /// <param name="premium">Discounted payoff P of the path.</param>
        /// <param name="logSpotAdjoint">Log-spot adjoints, <c>steps + 1</c> entries (input and output).</param>
        /// <param name="adjointOffset">Index of the pillar-0 entry.</param>
        /// <param name="gradient">Accumulator, <see cref="GradientLength"/> entries from <paramref name="gradientOffset"/>.</param>
        /// <param name="gradientOffset">Index of the spot entry.</param>
        [Kernel]
        public static void Reverse(double spot, double kappa, double theta, double xi, double rho, double[] dt, int steps,
            double[] normals, int normalOffset, double[] variance, int varianceOffset, double premium,
            double[] logSpotAdjoint, int adjointOffset, double[] gradient, int gradientOffset)
        {
            double driverRho = -rho / Math.Sqrt(1.0 - rho * rho); // d sqrt(1 - rho^2) / d rho
            double vBar = 0.0; // adjoint of v_{k+1}: the payoff does not read the variance
            double kappaBar = 0.0, thetaBar = 0.0, xiBar = 0.0, rhoBar = 0.0, rateBar = 0.0, maturity = 0.0;
            for (int k = steps - 1; k >= 0; --k)
            {
                double xBar = logSpotAdjoint[adjointOffset + k + 1];
                double h = dt[k];
                double sqrtH = Math.Sqrt(h);
                double zSpot = normals[normalOffset + 2 * k];
                double zPerp = normals[normalOffset + 2 * k + 1];
                double zVariance = HestonStep.CorrelatedDriver(zSpot, zPerp, rho);
                double v = variance[varianceOffset + k];
                double vPlus = Math.Max(v, 0.0);
                double volatility = Math.Sqrt(vPlus);

                // v_{k+1} = v + kappa (theta - v+) h + xi sqrt(v+) sqrt(h) zV
                kappaBar += vBar * (theta - vPlus) * h;
                thetaBar += vBar * kappa * h;
                xiBar += vBar * volatility * sqrtH * zVariance;
                rhoBar += vBar * xi * volatility * sqrtH * (zSpot + driverRho * zPerp);
                // x_{k+1} = x + (r - v+ / 2) h + sqrt(v+) sqrt(h) zS
                rateBar += xBar * h;
                logSpotAdjoint[adjointOffset + k] += xBar;
                if (v > 0.0)
                {
                    double vPlusBar = -vBar * kappa * h - 0.5 * xBar * h;
                    double volatilityBar = vBar * xi * sqrtH * zVariance + xBar * sqrtH * zSpot;
                    vBar += vPlusBar + volatilityBar * 0.5 / volatility;
                }
                maturity += h;
            }
            gradient[gradientOffset + SpotIndex] += logSpotAdjoint[adjointOffset] / spot;
            gradient[gradientOffset + InitialVarianceIndex] += vBar;
            gradient[gradientOffset + KappaIndex] += kappaBar;
            gradient[gradientOffset + ThetaIndex] += thetaBar;
            gradient[gradientOffset + XiIndex] += xiBar;
            gradient[gradientOffset + CorrelationIndex] += rhoBar;
            gradient[gradientOffset + RateIndex] += rateBar - premium * maturity;
        }
    }
}
