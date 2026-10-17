using Hybridizer.Runtime.CUDAImports;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Per-step arithmetic of the Heston model, on plain doubles so that it can be called from kernels
    /// and tested without the Hybridizer runtime.
    ///
    /// $$
    /// dS_t / S_t = r dt + \sqrt{v_t} dW^S_t, \qquad dv_t = \kappa ( \theta - v_t ) dt + \xi \sqrt{v_t} dW^v_t, \qquad d\langle W^S, W^v \rangle_t = \rho dt
    /// $$
    ///
    /// Variance is discretised with the full-truncation Euler scheme: the raw Euler variance $v$ is kept
    /// (it may become negative) and only $v^+ = \max(v, 0)$ enters the drift, the diffusion and the spot.
    /// <see href="https://doi.org/10.1080/14697680802392496">Lord, Koekkoek, van Dijk (2010), A comparison of biased simulation schemes for stochastic volatility models</see>
    ///
    /// All Gaussian inputs are standard normal draws N(0,1); the $\sqrt{dt}$ scaling is applied here.
    /// </summary>
    public static class HestonStep
    {
        /// <summary>
        /// Variance driver correlated with the spot driver: $Z_v = \rho Z_S + \sqrt{1 - \rho^2} Z_\perp$.
        /// </summary>
        /// <param name="zSpot">Standard normal draw driving the spot</param>
        /// <param name="zIndependent">Standard normal draw independent from <paramref name="zSpot"/></param>
        /// <param name="rho">Spot/variance correlation, in [-1, 1]</param>
        [Kernel]
        public static double CorrelatedDriver(double zSpot, double zIndependent, double rho)
        {
            return rho * zSpot + Math.Sqrt(1.0 - rho * rho) * zIndependent;
        }

        /// <summary>
        /// Full-truncation Euler step of the variance:
        /// $v_{t+dt} = v_t + \kappa (\theta - v_t^+) dt + \xi \sqrt{v_t^+} \sqrt{dt} Z_v$.
        /// </summary>
        /// <param name="v">Raw (possibly negative) variance at the start of the step</param>
        /// <param name="kappa">Mean-reversion speed $\kappa$</param>
        /// <param name="theta">Long-term variance $\theta$</param>
        /// <param name="xi">Volatility of variance $\xi$</param>
        /// <param name="dt">Time step, in years</param>
        /// <param name="sqrtDt">$\sqrt{dt}$</param>
        /// <param name="zVariance">Standard normal draw driving the variance (see <see cref="CorrelatedDriver"/>)</param>
        /// <returns>Raw variance at the end of the step; callers must keep it raw and truncate on use</returns>
        [Kernel]
        public static double FullTruncationEuler(double v, double kappa, double theta, double xi, double dt, double sqrtDt, double zVariance)
        {
            double vPlus = Math.Max(v, 0.0);
            return v + kappa * (theta - vPlus) * dt + xi * Math.Sqrt(vPlus) * sqrtDt * zVariance;
        }

        /// <summary>
        /// Log-Euler step of the spot with the truncated variance:
        /// $\ln S_{t+dt} = \ln S_t + (r - v_t^+ / 2) dt + \sqrt{v_t^+} \sqrt{dt} Z_S$.
        /// This is the step <see cref="EquityDiffusion{Volatility, Rate, LogSpot, Brownian, Schedule}"/> performs
        /// with $\sigma = $ <see cref="Volatility"/>$(v_t)$.
        /// </summary>
        /// <param name="lnS">Log-spot at the start of the step</param>
        /// <param name="v">Raw (possibly negative) variance at the start of the step</param>
        /// <param name="rate">Risk-free rate over the step</param>
        /// <param name="dt">Time step, in years</param>
        /// <param name="sqrtDt">$\sqrt{dt}$</param>
        /// <param name="zSpot">Standard normal draw driving the spot</param>
        [Kernel]
        public static double LogSpotEuler(double lnS, double v, double rate, double dt, double sqrtDt, double zSpot)
        {
            double vPlus = Math.Max(v, 0.0);
            return lnS + (rate - 0.5 * vPlus) * dt + Math.Sqrt(vPlus) * sqrtDt * zSpot;
        }

        /// <summary>
        /// Instantaneous volatility $\sigma = \sqrt{v^+}$ of a raw variance.
        /// </summary>
        [Kernel]
        public static double Volatility(double v)
        {
            return Math.Sqrt(Math.Max(v, 0.0));
        }
    }
}
