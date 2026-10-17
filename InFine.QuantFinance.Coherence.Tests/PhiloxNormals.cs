using System;
using InFine.QuantFinance.Models.Concepts;
using InFine.QuantFinance.Random;
using InFine.QuantFinance.Random.DotNet;
using InFine.QuantFinance.Scheduling;

namespace InFine.QuantFinance.Coherence.Tests
{
    /// <summary>
    /// Standard normal draws of one path, from the Philox sub-stream (seed, path, stream).
    /// The draws of a path depend on nothing but those three numbers, which is what makes the Brownian
    /// arrays independent from the way paths are distributed over threads, blocks or devices.
    /// </summary>
    public static class PhiloxNormals
    {
        /// <summary>
        /// Writes the first <paramref name="count"/> Box-Muller normals of the sub-stream (seed, path, stream)
        /// into <paramref name="destination"/>[0 .. count).
        /// </summary>
        public static void FillPath(ulong seed, uint path, uint stream, double[] destination, int count)
        {
            var generator = new PhiloxGenerator(seed, path, stream);
            int even = (count + 1) & ~1; // Box-Muller produces pairs
            double[] draws = even == count ? destination : new double[even];
            generator.GenerateGaussians(GaussianTransformEnum.BoxMuller, 0.0, 1.0, even, draws);
            if (!ReferenceEquals(draws, destination))
                Array.Copy(draws, destination, count);
        }
    }

    /// <summary>
    /// Fills the Brownian array of the paths [simFrom, simTo) from Philox: brownian[path, t] is draw t of the
    /// sub-stream (seed, path, stream). Being an <see cref="ISimulatable"/>, the fill itself can be distributed by any
    /// <see cref="ExecutorBackend"/>, and must give the same array whatever the chunking.
    /// </summary>
    public sealed class PhiloxBrownianFill<TBrownian> : ISimulatable
        where TBrownian : IBrownian
    {
        private readonly ulong _seed;
        private readonly uint _stream;
        private readonly int _steps;
        private readonly TBrownian _brownian;

        public PhiloxBrownianFill(ulong seed, uint stream, int steps, TBrownian brownian)
        {
            _seed = seed;
            _stream = stream;
            _steps = steps;
            _brownian = brownian;
        }

        public void Simulate(int simFrom, int simTo)
        {
            var draws = new double[_steps];
            for (int path = simFrom; path < simTo; ++path)
            {
                PhiloxNormals.FillPath(_seed, (uint)path, _stream, draws, _steps);
                for (int t = 0; t < _steps; ++t)
                    _brownian[path, t] = draws[t];
            }
        }
    }

    /// <summary>
    /// Same fill into a plain row-major [path * steps + t] array: no Hybridizer type involved,
    /// so it runs on any host.
    /// </summary>
    public sealed class PhiloxPlainFill : ISimulatable
    {
        private readonly ulong _seed;
        private readonly uint _stream;
        private readonly int _steps;
        private readonly double[] _target;

        public PhiloxPlainFill(ulong seed, uint stream, int steps, double[] target)
        {
            _seed = seed;
            _stream = stream;
            _steps = steps;
            _target = target;
        }

        public void Simulate(int simFrom, int simTo)
        {
            var draws = new double[_steps];
            for (int path = simFrom; path < simTo; ++path)
            {
                PhiloxNormals.FillPath(_seed, (uint)path, _stream, draws, _steps);
                Array.Copy(draws, 0, _target, (long)path * _steps, _steps);
            }
        }
    }
}
