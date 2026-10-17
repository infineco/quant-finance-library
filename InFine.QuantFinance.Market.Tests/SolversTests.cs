using System;
using Xunit;

namespace InFine.QuantFinance.Market.Tests;

/// <summary>
/// Brent against roots known in closed form or to many published digits. The guaranteed accuracy is
/// <c>|b - root| &lt;= tol + 4 eps |b|</c>; the assertions use that bound.
/// </summary>
public class SolversTests
{
    private const double Tol = 1e-14;

    private static double Bound(double root) => Tol + 4.0 * 2.220446049250313e-16 * Math.Abs(root);

    [Fact]
    public void FindsSquareRootOfTwo()
    {
        var root = Solvers.Brent(x => x * x - 2.0, 0.0, 2.0, Tol);
        Assert.InRange(Math.Abs(root - Math.Sqrt(2.0)), 0.0, Bound(root));
    }

    [Fact]
    public void FindsWallisCubicRoot()
    {
        // x^3 - 2x - 5 = 0 (Wallis' example, used by Newton): 2.0945514815423265914823865405793...
        var root = Solvers.Brent(x => x * x * x - 2.0 * x - 5.0, 2.0, 3.0, Tol);
        Assert.InRange(Math.Abs(root - 2.0945514815423266), 0.0, Bound(root));
    }

    [Fact]
    public void FindsDottieNumber()
    {
        // cos x = x: 0.73908513321516064165531208767387... (OEIS A003957).
        var root = Solvers.Brent(x => Math.Cos(x) - x, -1.0, 2.0, Tol);
        Assert.InRange(Math.Abs(root - 0.7390851332151607), 0.0, Bound(root));
    }

    [Fact]
    public void FindsLogarithmWithReversedBracket()
    {
        // The bracket ends may come in either order.
        var root = Solvers.Brent(x => Math.Exp(x) - 2.0, 5.0, -5.0, Tol);
        Assert.InRange(Math.Abs(root - Math.Log(2.0)), 0.0, Bound(root));
    }

    [Fact]
    public void ConvergesSuperlinearlyOnSmoothFunctions()
    {
        // Bisection would need log2(2 / 1e-14) ~ 48 evaluations; Brent's interpolation steps need far fewer.
        var calls = 0;
        Solvers.Brent(x => { calls++; return x * x * x - 2.0 * x - 5.0; }, 2.0, 3.0, Tol);
        Assert.InRange(calls, 3, 15);
    }

    [Fact]
    public void LocatesAJumpByFallingBackToBisection()
    {
        // No root, a sign change at 0.3: the bracket still shrinks onto the discontinuity.
        var root = Solvers.Brent(x => x < 0.3 ? -1.0 : 1.0, 0.0, 1.0, Tol);
        Assert.InRange(Math.Abs(root - 0.3), 0.0, Bound(root) + 1e-16);
    }

    [Fact]
    public void ReturnsAnEndPointThatIsARoot()
    {
        Assert.Equal(1.0, Solvers.Brent(x => x - 1.0, 1.0, 3.0, Tol));
        Assert.Equal(3.0, Solvers.Brent(x => x - 3.0, 1.0, 3.0, Tol));
    }

    [Fact]
    public void RejectsAnIntervalWithoutSignChange()
    {
        Assert.Throws<ArgumentException>(() => Solvers.Brent(x => x * x + 1.0, -1.0, 1.0, Tol));
    }

    [Fact]
    public void RejectsNonFiniteFunctionValues()
    {
        Assert.Throws<InvalidOperationException>(() => Solvers.Brent(x => x < 0.5 ? double.NaN : 1.0, 0.0, 1.0, Tol));
    }

    [Fact]
    public void BracketingWidensUntilTheSignChanges()
    {
        double lower = 0.0, upper = 0.1;
        Assert.True(Solvers.TryBracket(x => x - 7.0, ref lower, ref upper));
        Assert.True(lower <= 7.0 && 7.0 <= upper);
        var root = Solvers.BracketAndBrent(x => Math.Exp(x) - 1000.0, 0.0, 0.01, Tol);
        Assert.InRange(Math.Abs(root - Math.Log(1000.0)), 0.0, Bound(root));
        Assert.Throws<InvalidOperationException>(() => Solvers.BracketAndBrent(x => x * x + 1.0, 0.0, 0.01, Tol));
    }
}
