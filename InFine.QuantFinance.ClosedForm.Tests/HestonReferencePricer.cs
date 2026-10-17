using System;
using System.Numerics;

namespace InFine.QuantFinance.ClosedForm.Tests
{
    /// <summary>
    /// Independent test oracle for Heston European prices: Gil-Pelaez inversion of the characteristic function of
    /// ln S_T in the "little Heston trap" form (Albrecher et al. 2007), adapted from the test-only pricer of
    /// InFine.QuantFinance.Models.Tests (UnitTest_Heston.cs). It shares no code with the library: it uses
    /// <see cref="System.Numerics.Complex"/>, Heston's direct formula for C and D (no rewriting for small xi), and
    /// adaptive composite Gauss-Legendre quadrature instead of a cosine expansion.
    /// </summary>
    /// <remarks>
    /// <c>C = F P1 - K P2</c> (undiscounted), <c>P_j = 1/2 + 1/pi int_0^inf Re[e^{-i u ln K} f_j(u) / (i u)] du</c>,
    /// <c>f_2(u) = phi(u)</c>, <c>f_1(u) = phi(u - i) / F</c>. The integrands are smooth at 0 and decay
    /// exponentially; panels of width 0.5 with 20 Gauss-Legendre points are added until ten consecutive panels
    /// contribute less than 1e-17 each (or u reaches 20000). Accuracy is around 1e-12 on the cases tested.
    /// </remarks>
    static class HestonReferencePricer
    {
        const int Order = 20;
        const double PanelWidth = 0.5;
        static readonly double[] Nodes = new double[Order], Weights = new double[Order];

        static HestonReferencePricer()
        {
            // Gauss-Legendre nodes on [-1, 1] by Newton's method on P_n
            for (int i = 0; i < Order; i++)
            {
                double x = Math.Cos(Math.PI * (i + 0.75) / (Order + 0.5)), dp = 0.0;
                for (int iter = 0; iter < 100; iter++)
                {
                    double p0 = 1.0, p1 = x;
                    for (int k = 2; k <= Order; k++)
                    {
                        double p2 = ((2 * k - 1) * x * p1 - (k - 1) * p0) / k;
                        p0 = p1;
                        p1 = p2;
                    }
                    dp = Order * (x * p1 - p0) / (x * x - 1.0);
                    double dx = p1 / dp;
                    x -= dx;
                    if (Math.Abs(dx) < 1e-16) break;
                }
                Nodes[i] = x;
                Weights[i] = 2.0 / ((1.0 - x * x) * dp * dp);
            }
        }

        /// <summary>phi(u) = E[exp(i u ln S_T)] for complex u, with ln F = ln S0 + (r - q) T folded into lnForward.</summary>
        public static Complex Phi(Complex u, double lnForward, double t, double v0, double kappa, double theta, double xi, double rho)
        {
            Complex i = Complex.ImaginaryOne;
            double xi2 = xi * xi;
            Complex beta = kappa - rho * xi * i * u;
            Complex d = Complex.Sqrt(beta * beta + xi2 * (i * u + u * u));
            Complex g = (beta - d) / (beta + d);
            Complex edt = Complex.Exp(-d * t);
            Complex c = kappa * theta / xi2 * ((beta - d) * t - 2.0 * Complex.Log((1.0 - g * edt) / (1.0 - g)));
            Complex dd = (beta - d) / xi2 * (1.0 - edt) / (1.0 - g * edt);
            return Complex.Exp(i * u * lnForward + c + dd * v0);
        }

        /// <summary>Undiscounted call on the forward F.</summary>
        public static double UndiscountedCall(double forward, double strike, double t,
            double v0, double kappa, double theta, double xi, double rho)
        {
            Complex i = Complex.ImaginaryOne;
            double k = Math.Log(strike), lnF = Math.Log(forward);
            double i1 = 0.0, i2 = 0.0;
            int quiet = 0;
            for (double lo = 0.0; lo < 20000.0 && quiet < 10; lo += PanelWidth)
            {
                double s1 = 0.0, s2 = 0.0;
                for (int n = 0; n < Order; n++)
                {
                    double u = lo + 0.5 * PanelWidth * (1.0 + Nodes[n]);
                    double w = 0.5 * PanelWidth * Weights[n];
                    Complex e = Complex.Exp(-i * u * k) / (i * u);
                    s1 += w * (e * Phi(u - i, lnF, t, v0, kappa, theta, xi, rho) / forward).Real;
                    s2 += w * (e * Phi(u, lnF, t, v0, kappa, theta, xi, rho)).Real;
                }
                i1 += s1;
                i2 += s2;
                quiet = Math.Abs(s1) + Math.Abs(s2) < 1e-17 ? quiet + 1 : 0;
            }
            double p1 = 0.5 + i1 / Math.PI;
            double p2 = 0.5 + i2 / Math.PI;
            return forward * p1 - strike * p2;
        }

        public static double Call(double forward, double strike, double df, double t,
            double v0, double kappa, double theta, double xi, double rho)
        {
            return df * UndiscountedCall(forward, strike, t, v0, kappa, theta, xi, rho);
        }

        public static double Put(double forward, double strike, double df, double t,
            double v0, double kappa, double theta, double xi, double rho)
        {
            return df * (UndiscountedCall(forward, strike, t, v0, kappa, theta, xi, rho) - forward + strike);
        }
    }
}
