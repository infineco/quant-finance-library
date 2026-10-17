using BenchmarkDotNet.Attributes;
using InFine.QuantFinance.ClosedForm;

namespace InFine.QuantFinance.Benchmarks
{
    /// <summary>
    /// Black-Scholes-Merton closed form and normal CDF, scalar managed code (CPU).
    /// Successor of the legacy BlackScholesClosedForm.Run_DotNet_Double.
    /// </summary>
    [MemoryDiagnoser]
    public class ClosedFormBenchmarks
    {
        [Params(1024, 65536, 1048576)]
        public int N;

        private double[] _spot, _strike, _maturity, _call, _put, _x;
        private const double Rate = 0.01;
        private const double Sigma = 0.45;

        [GlobalSetup]
        public void Setup()
        {
            _spot = new double[N];
            _strike = new double[N];
            _maturity = new double[N];
            _call = new double[N];
            _put = new double[N];
            _x = new double[N];
            var rng = new System.Random(20151114);
            for (int i = 0; i < N; ++i)
            {
                _spot[i] = 80.0 + 40.0 * rng.NextDouble();
                _strike[i] = 100.0;
                _maturity[i] = 0.25 + 2.0 * rng.NextDouble();
                _x[i] = -8.0 + 16.0 * rng.NextDouble();
            }
        }

        [Benchmark]
        public double BlackScholesCallPut_ScalarLoop()
        {
            for (int i = 0; i < N; ++i)
                BlackScholesMertonPrimitive.CallPut(out _call[i], out _put[i], _spot[i], _strike[i], Rate, Sigma, _maturity[i]);
            return _call[N - 1] + _put[N - 1];
        }

        [Benchmark]
        public double NormalCdf()
        {
            double acc = 0.0;
            for (int i = 0; i < N; ++i)
                acc += NormalDistribution.Cdf(_x[i]);
            return acc;
        }
    }
}
