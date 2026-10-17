using Hybridizer.Runtime.CUDAImports;
using InFine.QuantFinance.ClosedForm;
using System;

namespace InFine.QuantFinance.Models.Diffusion
{
    /// <summary>
    /// Exact one-step transition of the Hull-White state, on plain doubles so that it can be called from kernels and tested
    /// without the Hybridizer runtime.
    /// <para>
    /// With <c>r = x + phi</c> (see <see cref="HullWhiteKernels"/>), <c>dx = -a x dt + sigma dW</c>. Over a step of length
    /// <c>dt</c>, the pair (<c>x(t + dt)</c>, <c>Y = int_t^{t+dt} x ds</c>) given <c>x(t)</c> is Gaussian with means
    /// <c>x(t) e^{-a dt}</c> and <c>x(t) B(dt)</c>, variances <c>V_x = sigma^2 (1 - e^{-2 a dt}) / (2 a)</c> and
    /// <see cref="HullWhiteKernels.IntegralVariance"/>, and covariance <c>sigma^2 B(dt)^2 / 2</c>. It is drawn from two
    /// independent standard normals through the lower Cholesky factor <c>(l11, 0; l21, l22)</c> of that 2x2 covariance:
    /// <c>x' = e^{-a dt} x + l11 z1</c>, <c>Y = B(dt) x + l21 z1 + l22 z2</c>. There is no discretisation error at all, so the
    /// bank account <c>exp(int r ds)</c> is exact on any grid.
    /// </para>
    /// </summary>
    public static class HullWhiteStep
    {
        /// <summary>
        /// Coefficients of a step: <c>decay = e^{-a dt}</c>, <c>loading = B(dt)</c> and the Cholesky factor. With
        /// <c>sigma = 0</c> (or <c>dt = 0</c>) the factor is zero.
        /// </summary>
        public static void Coefficients(double a, double sigma, double dt,
            out double decay, out double loading, out double l11, out double l21, out double l22)
        {
            decay = Math.Exp(-a * dt);
            loading = HullWhiteKernels.B(a, dt);
            double vx = HullWhiteKernels.StateVariance(a, sigma, dt);
            double vy = HullWhiteKernels.IntegralVariance(a, sigma, dt);
            double cov = HullWhiteKernels.StateIntegralCovariance(a, sigma, dt);
            if (vx > 0.0)
            {
                l11 = Math.Sqrt(vx);
                l21 = cov / l11;
                l22 = Math.Sqrt(Math.Max(0.0, vy - l21 * l21));
            }
            else
            {
                l11 = 0.0;
                l21 = 0.0;
                l22 = Math.Sqrt(Math.Max(0.0, vy));
            }
        }

        /// <summary>State at the end of the step: <c>decay x + l11 z1</c>.</summary>
        [Kernel]
        public static double NextState(double x, double z1, double decay, double l11)
        {
            return decay * x + l11 * z1;
        }

        /// <summary>Integral of the state over the step: <c>loading x + l21 z1 + l22 z2</c>.</summary>
        [Kernel]
        public static double IntegralIncrement(double x, double z1, double z2, double loading, double l21, double l22)
        {
            return loading * x + l21 * z1 + l22 * z2;
        }
    }
}
