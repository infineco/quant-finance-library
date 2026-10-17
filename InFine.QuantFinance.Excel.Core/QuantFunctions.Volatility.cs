using System;
using System.Collections.Generic;
using InFine.QuantFinance.Volatility;

namespace InFine.QuantFinance.Excel.Core
{
    /// <summary>An SVI fit of one smile and the single-maturity surface built on it, as held in the object cache.</summary>
    public sealed class SviSmile
    {
        public SviSmile(SviCalibrationResult calibration, SviVolatilitySurface surface)
        {
            Calibration = calibration;
            Surface = surface;
        }

        public SviCalibrationResult Calibration { get; }

        public SviVolatilitySurface Surface { get; }
    }

    public sealed partial class QuantFunctions
    {
        /// <summary>Object kind of the volatility surfaces held in the cache.</summary>
        public const string SurfaceKind = "QF.Surface";

        /// <summary>
        /// QF.SVI.FIT(name, T, forward, strikes, vols) - fits a raw-SVI smile to Black implied volatilities quoted at the
        /// given strikes for expiry T (years) and forward, and returns the surface handle.
        /// </summary>
        public object SviFit(object? name, object? maturity, object? forward, object? strikes, object? vols) =>
            Run("QF.SVI.FIT", () =>
            {
                string surfaceName = Arg.Text(name, "name");
                double t = Arg.Double(maturity, "T");
                double f = Arg.Double(forward, "forward");
                double[] k = Arg.Doubles(strikes, "strikes");
                double[] v = Arg.Doubles(vols, "vols");
                if (k.Length != v.Length)
                    throw new XlException(XlError.Value, $"strikes and vols must have the same length ({k.Length} and {v.Length})");

                var fit = SviCalibrator.Calibrate(new SmileQuotes(t, f, k, v));
                if (!fit.Converged)
                    throw new XlException(XlError.Num,
                        $"the SVI fit did not converge (status {fit.Status}, volatility error up to {fit.MaxAbsoluteVolatilityError:G3})");
                var surface = new SviVolatilitySurface(new ConstantForwardCurve(f), new[] { t }, new[] { fit.Parameters });

                var p = fit.Parameters;
                var details = new List<KeyValuePair<string, object>>
                {
                    new("Model", "raw SVI"),
                    new("Maturity", t),
                    new("Forward", f),
                    new("a", p.A),
                    new("b", p.B),
                    new("rho", p.Rho),
                    new("m", p.M),
                    new("sigma", p.Sigma),
                    new("Quotes", (double)k.Length),
                    new("RMSE total variance", fit.RootMeanSquareTotalVarianceError),
                    new("Max vol error", fit.MaxAbsoluteVolatilityError),
                    new("Status", fit.Status.ToString()),
                    new("Iterations", (double)fit.Iterations),
                };
                return Cache.Put(SurfaceKind, surfaceName, new SviSmile(fit, surface),
                    Fingerprint("svi", t, f, k, v),
                    $"SVI fit of {k.Length} quotes at T={t:G6}", details);
            });

        /// <summary>QF.SURFACE.VOL(handle, T, K) - Black implied volatility of a fitted surface.</summary>
        public object SurfaceVol(object? handle, object? maturity, object? strike) =>
            Run("QF.SURFACE.VOL", () =>
            {
                var smile = Cache.Get<SviSmile>(Arg.Text(handle, "handle"));
                return Finite(smile.Surface.ImpliedVolatility(Arg.Double(maturity, "T"), Arg.Double(strike, "K")));
            });
    }
}
