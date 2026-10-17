using System;
using System.Collections.Generic;

namespace InFine.QuantFinance.Market
{
    /// <summary>The outcome of <see cref="PiecewiseYieldCurveBuilder.Bootstrap"/>.</summary>
    /// <param name="Curve">The bootstrapped curve, one pillar per instrument at its maturity date.</param>
    /// <param name="Passes">Number of sweeps over the instruments: 1 for local interpolations, at least 2 otherwise.</param>
    /// <param name="LastPassMaxChange">Largest change of a pillar zero rate during the last sweep (0 after a single sweep).</param>
    /// <param name="MaxRepricingError">Largest <c>|implied rate - quote|</c> over the instruments on the final curve.</param>
    public sealed record BootstrapResult(InterpolatedDiscountCurve Curve, int Passes, double LastPassMaxChange, double MaxRepricingError);

    /// <summary>
    /// Bootstraps an <see cref="InterpolatedDiscountCurve"/> so that it reprices a set of quoted instruments (deposits,
    /// FRAs, vanilla swaps...), one pillar per instrument at its <see cref="ICurveInstrument.MaturityDate"/>.
    /// <para>
    /// Single-curve framework: the same curve discounts every cash flow and projects every floating rate (see
    /// <see cref="VanillaSwap"/>). Multi-curve stripping (OIS discounting plus forecast curves) is out of scope; it would
    /// solve a forecast curve with instruments priced against a fixed, separately built discount curve.
    /// </para>
    /// <para>
    /// Algorithm. The unknown at pillar <c>k</c> is its continuously compounded zero rate <c>z_k</c>
    /// (<c>P(0, t_k) = exp(-z_k t_k)</c>), solved with <see cref="Solvers.Brent"/> (absolute tolerance
    /// <see cref="SolverTolerance"/>, 1e-14 by default) so that the instrument's implied rate equals its quote. A first sweep
    /// solves the pillars in maturity order on the curve made of the pillars solved so far; with a local interpolation
    /// (<see cref="CurveInterpolation.IsLocal"/>) every instrument depends only on pillars up to its own, so that sweep is
    /// exact. With a non-local one (the monotone cubic) a pillar moves the curve before the previous pillars too, so
    /// further sweeps re-solve every pillar on the full curve (later pillars at their previous values) until no zero rate
    /// moves by more than <see cref="ConvergenceTolerance"/> (1e-12 by default) during a sweep.
    /// </para>
    /// </summary>
    public sealed class PiecewiseYieldCurveBuilder
    {
        private readonly List<(ICurveInstrument Instrument, double Quote)> _inputs = new();
        private CurveInterpolation _interpolation = CurveInterpolation.LogLinearDiscount;

        /// <summary>Starts a curve seen from <paramref name="referenceDate"/>, measuring time with <paramref name="dayCounter"/>.</summary>
        public PiecewiseYieldCurveBuilder(DateOnly referenceDate, IDayCounter dayCounter)
        {
            ArgumentNullException.ThrowIfNull(dayCounter);
            ReferenceDate = referenceDate;
            DayCounter = dayCounter;
        }

        /// <summary>The reference date of the curve.</summary>
        public DateOnly ReferenceDate { get; }

        /// <summary>The time-measuring day counter of the curve.</summary>
        public IDayCounter DayCounter { get; }

        /// <summary>Absolute Brent tolerance on each pillar zero rate.</summary>
        public double SolverTolerance { get; private set; } = 1e-14;

        /// <summary>Largest zero-rate change in a sweep at which global iteration stops.</summary>
        public double ConvergenceTolerance { get; private set; } = 1e-12;

        /// <summary>Maximum number of sweeps before giving up.</summary>
        public int MaxPasses { get; private set; } = 100;

        /// <summary>Sets the interpolation (default: log-linear on discount factors).</summary>
        public PiecewiseYieldCurveBuilder WithInterpolation(CurveInterpolation interpolation)
        {
            ArgumentNullException.ThrowIfNull(interpolation);
            _interpolation = interpolation;
            return this;
        }

        /// <summary>Sets the Brent tolerance on pillar zero rates.</summary>
        public PiecewiseYieldCurveBuilder WithSolverTolerance(double tolerance)
        {
            if (!(tolerance > 0.0))
                throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive.");
            SolverTolerance = tolerance;
            return this;
        }

        /// <summary>Sets the global-iteration stopping rule.</summary>
        public PiecewiseYieldCurveBuilder WithConvergence(double tolerance, int maxPasses)
        {
            if (!(tolerance > 0.0))
                throw new ArgumentOutOfRangeException(nameof(tolerance), tolerance, "The tolerance must be positive.");
            if (maxPasses < 2)
                throw new ArgumentOutOfRangeException(nameof(maxPasses), maxPasses, "At least two passes are needed to measure convergence.");
            ConvergenceTolerance = tolerance;
            MaxPasses = maxPasses;
            return this;
        }

        /// <summary>Adds an instrument and its market quote (e.g. 0.0325 for 3.25%).</summary>
        public PiecewiseYieldCurveBuilder Add(ICurveInstrument instrument, double quote)
        {
            ArgumentNullException.ThrowIfNull(instrument);
            if (!double.IsFinite(quote))
                throw new ArgumentOutOfRangeException(nameof(quote), quote, "The quote must be finite.");
            _inputs.Add((instrument, quote));
            return this;
        }

        /// <summary>Bootstraps and returns the curve.</summary>
        public InterpolatedDiscountCurve Build() => Bootstrap().Curve;

        /// <summary>Bootstraps the curve and reports convergence diagnostics.</summary>
        /// <exception cref="InvalidOperationException">
        /// No instrument; two instruments share a pillar; a pillar is not after the reference date; a pillar cannot be
        /// solved; or global iteration did not converge within <see cref="MaxPasses"/>.
        /// </exception>
        public BootstrapResult Bootstrap()
        {
            if (_inputs.Count == 0)
                throw new InvalidOperationException("No instrument to bootstrap.");
            var inputs = _inputs.ToArray();
            Array.Sort(inputs, (a, b) => a.Instrument.MaturityDate.CompareTo(b.Instrument.MaturityDate));
            var n = inputs.Length;
            var dates = new DateOnly[n];
            var times = new double[n];
            for (var i = 0; i < n; i++)
            {
                dates[i] = inputs[i].Instrument.MaturityDate;
                times[i] = DayCounter.YearFraction(ReferenceDate, dates[i]);
                if (!(times[i] > (i == 0 ? 0.0 : times[i - 1])))
                    throw new InvalidOperationException(i > 0 && dates[i] == dates[i - 1]
                        ? $"Two instruments mature on {dates[i]}; each pillar needs exactly one instrument."
                        : $"Pillar {dates[i]} is not after the reference date {ReferenceDate} and the previous pillar under {DayCounter.Name}.");
            }

            var zeros = new double[n];
            // First sweep: pillar k on the curve of pillars 0..k, starting from the previous zero rate.
            for (var k = 0; k < n; k++)
                zeros[k] = Solve(inputs, dates, times, zeros, k, k + 1, k == 0 ? inputs[0].Quote : zeros[k - 1]);

            var passes = 1;
            var lastChange = 0.0;
            if (!_interpolation.IsLocal && n > 1)
            {
                while (true)
                {
                    passes++;
                    lastChange = 0.0;
                    for (var k = 0; k < n; k++)
                    {
                        var previous = zeros[k];
                        zeros[k] = Solve(inputs, dates, times, zeros, k, n, previous);
                        lastChange = Math.Max(lastChange, Math.Abs(zeros[k] - previous));
                    }
                    if (lastChange <= ConvergenceTolerance) break;
                    if (passes >= MaxPasses)
                        throw new InvalidOperationException(
                            $"Global bootstrap did not converge in {MaxPasses} passes (last zero-rate change {lastChange}).");
                }
            }

            var curve = CreateCurve(dates, times, zeros, n);
            var maxError = 0.0;
            foreach (var (instrument, quote) in inputs)
                maxError = Math.Max(maxError, Math.Abs(instrument.ImpliedRate(curve) - quote));
            return new BootstrapResult(curve, passes, lastChange, maxError);
        }

        private double Solve((ICurveInstrument Instrument, double Quote)[] inputs, DateOnly[] dates, double[] times, double[] zeros,
            int k, int pillarCount, double guess)
        {
            var (instrument, quote) = inputs[k];
            double Objective(double z)
            {
                zeros[k] = z;
                return instrument.ImpliedRate(CreateCurve(dates, times, zeros, pillarCount)) - quote;
            }
            try
            {
                return Solvers.BracketAndBrent(Objective, guess, InitialBracketHalfWidth, SolverTolerance);
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException)
            {
                throw new InvalidOperationException($"Cannot solve the pillar {dates[k]} for the quote {quote}: {e.Message}", e);
            }
        }

        private InterpolatedDiscountCurve CreateCurve(DateOnly[] dates, double[] times, double[] zeros, int count)
        {
            var t = new double[count];
            var d = new DateOnly[count];
            var df = new double[count];
            for (var i = 0; i < count; i++)
            {
                t[i] = times[i];
                d[i] = dates[i];
                df[i] = Math.Exp(-zeros[i] * times[i]);
            }
            return new InterpolatedDiscountCurve(ReferenceDate, DayCounter, t, d, df, _interpolation);
        }

        // Half-width of the first bracket around the guess (50 bp); TryBracket widens it geometrically if needed.
        private const double InitialBracketHalfWidth = 0.005;
    }
}
