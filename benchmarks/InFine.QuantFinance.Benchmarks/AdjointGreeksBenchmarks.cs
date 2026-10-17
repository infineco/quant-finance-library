using System;
using BenchmarkDotNet.Attributes;
using InFine.QuantFinance.Arithmetic.AutoDiff;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Models.Diffusion;
using InFine.QuantFinance.Payoffs.Equity;
using InFine.QuantFinance.Random.DotNet;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Cost of Monte Carlo Greeks of an arithmetic basket call on D = 20 correlated log-normal assets, 50 steps:
    /// valuation only (the host reference of the diffusion kernel and the basket payoff), the adjoint
    /// (<see cref="CorrelatedLogNormalAdjoint"/> + <see cref="AdjointGreeks.Basket"/>: price and all 3 D + 50 = 110
    /// sensitivities in one backward sweep per path), and tangent mode (<see cref="DualLogNormalMath.DiffusePath"/>
    /// with four inputs per <see cref="Dual4"/> pass, whole path recomputed per pass) for 4, 40 (deltas and vegas) and
    /// 110 inputs. Same pre-generated Philox draws for every method, so the random numbers are not timed.
    /// Plain managed code, no Hybridizer type: runs on every host.
    /// </summary>
    [MemoryDiagnoser]
    public class AdjointGreeksBenchmarks
    {
        const int Assets = 20, Steps = 50, Paths = 1000;
        const double Maturity = 1.0, Strike = 1.0;

        double[] _spot, _logSpot0, _sigma, _dividend, _rates, _dt, _lower, _weights, _reference, _spotT;
        double[] _scratch, _gradient;
        double[][] _normals;
        double _discount;
        Dual4[] _dualLogSpot0, _dualSigma, _dualDividend, _dualRates, _dualLogPath, _dualSpotT;
        double[] _tangents;

        [GlobalSetup]
        public void Setup()
        {
            _spot = new double[Assets];
            _logSpot0 = new double[Assets];
            _sigma = new double[Assets];
            _dividend = new double[Assets];
            _weights = new double[Assets];
            for (int d = 0; d < Assets; ++d)
            {
                _spot[d] = 90.0 + d;
                _logSpot0[d] = Math.Log(_spot[d]);
                _sigma[d] = 0.15 + 0.01 * d;
                _dividend[d] = 0.01;
                _weights[d] = 1.0 / Assets;
            }
            _reference = (double[])_spot.Clone();
            _rates = new double[Steps];
            _dt = new double[Steps];
            double integrated = 0.0;
            for (int t = 0; t < Steps; ++t)
            {
                _rates[t] = 0.02 + 0.0002 * t;
                _dt[t] = Maturity / Steps;
                integrated += _rates[t] * _dt[t];
            }
            _discount = Math.Exp(-integrated);
            var correlation = new double[Assets, Assets];
            for (int i = 0; i < Assets; ++i)
                for (int j = 0; j < Assets; ++j)
                    correlation[i, j] = i == j ? 1.0 : 0.4;
            _lower = CholeskyCorrelation.FromCorrelation(correlation).Lower;

            _normals = new double[Paths][];
            for (int p = 0; p < Paths; ++p)
            {
                _normals[p] = new double[Steps * Assets];
                FillNormals((uint)p, _normals[p]);
            }

            int pathLength = (Steps + 1) * Assets;
            _scratch = new double[2 * pathLength + Steps * Assets];
            _gradient = new double[CorrelatedLogNormalAdjoint.GradientLength(Assets, Steps)];
            _spotT = new double[Assets];
            _dualLogSpot0 = new Dual4[Assets];
            _dualSigma = new Dual4[Assets];
            _dualDividend = new Dual4[Assets];
            _dualRates = new Dual4[Steps];
            _dualLogPath = new Dual4[pathLength];
            _dualSpotT = new Dual4[Assets];
            _tangents = new double[CorrelatedLogNormalAdjoint.GradientLength(Assets, Steps)];
        }

        static void FillNormals(uint path, double[] z)
        {
            const ulong seed = 0x5EED_0048_AD10_1E57UL;
            for (int block = 0, k = 0; k < z.Length; ++block)
            {
                Philox4x32x10.GenerateBlock(seed, (ulong)block, path, 0, out uint r0, out uint r1, out uint r2, out uint r3);
                Philox4x32x10.BoxMuller(r0, r1, out double z0, out double z1);
                Philox4x32x10.BoxMuller(r2, r3, out double z2, out double z3);
                if (k < z.Length) z[k++] = z0;
                if (k < z.Length) z[k++] = z1;
                if (k < z.Length) z[k++] = z2;
                if (k < z.Length) z[k++] = z3;
            }
        }

        /// <summary>Price only: <see cref="CorrelatedLogNormalMath.DiffusePath"/>, spots at maturity, basket payoff.</summary>
        [Benchmark(Baseline = true)]
        public double Valuation()
        {
            double sum = 0.0;
            int last = Steps * Assets;
            for (int p = 0; p < Paths; ++p)
            {
                CorrelatedLogNormalMath.DiffusePath(_logSpot0, _sigma, _dividend, _lower, Assets, _dt, _rates, Steps, _normals[p], _scratch);
                for (int d = 0; d < Assets; ++d)
                    _spotT[d] = Math.Exp(_scratch[last + d]);
                sum += _discount * MultiAssetMath.BasketPathPayoff(_spotT, Assets, 0, _weights, _reference, +1, false, Strike);
            }
            return sum / Paths;
        }

        /// <summary>Price and the 110 sensitivities: forward sweep, payoff adjoint, backward sweep per path.</summary>
        [Benchmark]
        public double Adjoint()
        {
            int pathLength = (Steps + 1) * Assets, correlated = pathLength, adjoint = pathLength + Steps * Assets;
            Array.Clear(_gradient, 0, _gradient.Length);
            double sum = 0.0;
            for (int p = 0; p < Paths; ++p)
            {
                double df = CorrelatedLogNormalAdjoint.Forward(_spot, _sigma, _dividend, _lower, Assets, _dt, _rates, Steps,
                    _normals[p], 0, _scratch, 0, _scratch, correlated);
                double premium = AdjointGreeks.Basket(+1, Strike, _weights, _reference, Assets, df, _scratch, 0, Steps, _scratch, adjoint);
                CorrelatedLogNormalAdjoint.Reverse(_spot, _sigma, Assets, _dt, Steps, _scratch, correlated, premium,
                    _scratch, adjoint, _gradient, 0);
                sum += premium;
            }
            return sum / Paths + _gradient[0] / Paths;
        }

        /// <summary>One <see cref="Dual4"/> pass per path: price and 4 sensitivities.</summary>
        [Benchmark]
        public double Tangent4Inputs() => Tangent(1);

        /// <summary>10 passes per path: the 20 deltas and 20 vegas.</summary>
        [Benchmark]
        public double Tangent40Inputs() => Tangent(10);

        /// <summary>28 passes per path: all 110 inputs of the adjoint.</summary>
        [Benchmark]
        public double Tangent110Inputs() => Tangent((CorrelatedLogNormalAdjoint.GradientLength(Assets, Steps) + Dual4.Width - 1) / Dual4.Width);

        /// <summary>Forward mode with inputs seeded four at a time in the gradient layout of the adjoint.</summary>
        double Tangent(int passes)
        {
            int inputs = _tangents.Length;
            Array.Clear(_tangents, 0, inputs);
            double sum = 0.0;
            for (int p = 0; p < Paths; ++p)
            {
                for (int pass = 0; pass < passes; ++pass)
                {
                    int first = pass * Dual4.Width;
                    for (int d = 0; d < Assets; ++d)
                    {
                        _dualLogSpot0[d] = Dual4.Log(Seed(_spot[d], CorrelatedLogNormalAdjoint.SpotIndex(d), first));
                        _dualSigma[d] = Seed(_sigma[d], CorrelatedLogNormalAdjoint.SigmaIndex(Assets, d), first);
                        _dualDividend[d] = Seed(_dividend[d], CorrelatedLogNormalAdjoint.DividendIndex(Assets, d), first);
                    }
                    for (int t = 0; t < Steps; ++t)
                        _dualRates[t] = Seed(_rates[t], CorrelatedLogNormalAdjoint.RateIndex(Assets, t), first);
                    DualLogNormalMath.DiffusePath(_dualLogSpot0, _dualSigma, _dualDividend, _lower, Assets, _dt, _dualRates, Steps,
                        _normals[p], 0, _dualLogPath);
                    for (int d = 0; d < Assets; ++d)
                        _dualSpotT[d] = Dual4.Exp(_dualLogPath[Steps * Assets + d]);
                    Dual4 integrated = 0.0;
                    for (int t = 0; t < Steps; ++t)
                        integrated += _dualRates[t] * _dt[t];
                    Dual4 premium = Dual4.Exp(-integrated)
                        * MultiAssetMath.BasketPathPayoff(_dualSpotT, Assets, 0, _weights, _reference, +1, false, Strike);
                    for (int j = 0; j < Dual4.Width && first + j < inputs; ++j)
                        _tangents[first + j] += premium.Tangent(j);
                    if (pass == 0) sum += premium.Value;
                }
            }
            return sum / Paths + _tangents[0] / Paths;
        }

        static Dual4 Seed(double value, int input, int first)
        {
            int direction = input - first;
            return direction >= 0 && direction < Dual4.Width ? Dual4.Variable(value, direction) : Dual4.Constant(value);
        }
    }
}
